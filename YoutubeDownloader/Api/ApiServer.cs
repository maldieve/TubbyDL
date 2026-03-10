using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gress;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeExplode.Videos.Streams;

namespace YoutubeDownloader.Api;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

public class DownloadJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Url { get; init; } = "";
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double Progress { get; set; }
    public string? OutputFilePath { get; set; }
    public string? Error { get; set; }
}

public static class ApiServer
{
    private static readonly ConcurrentDictionary<string, DownloadJob> Jobs = new();
    private static readonly SemaphoreSlim DownloadSemaphore = new(3, 3);
    private const string DownloadDirectory = @"d:\downloads";
    private const int Port = 5005;
    private const int MaxBatchSize = 3;

    public static void Start(CancellationToken cancellationToken = default)
    {
        Task.Run(() => RunAsync(cancellationToken), cancellationToken);
    }

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.Urls.Add($"http://localhost:{Port}");

        // Health check
        app.MapGet(
            "/health",
            () =>
                Results.Ok(
                    new
                    {
                        status = "ok",
                        service = "YoutubeDownloader API",
                        port = Port,
                    }
                )
        );

        // Start one or more download jobs (max 3 URLs per request)
        app.MapPost(
            "/download",
            ([FromBody] DownloadRequest request) =>
            {
                if (request.Urls is not { Count: > 0 })
                    return Results.BadRequest(
                        new { error = "urls list is required and must not be empty" }
                    );

                if (request.Urls.Count > MaxBatchSize)
                    return Results.BadRequest(
                        new { error = $"Maximum {MaxBatchSize} URLs per request" }
                    );

                var created = request
                    .Urls.Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(url =>
                    {
                        var job = new DownloadJob { Url = url };
                        Jobs[job.Id] = job;
                        _ = Task.Run(() => RunJobAsync(job, cancellationToken), cancellationToken);
                        return new
                        {
                            jobId = job.Id,
                            statusUrl = $"/status/{job.Id}",
                            url = job.Url,
                        };
                    })
                    .ToList();

                return Results.Accepted("/status", new { jobs = created });
            }
        );

        // Check status for a list of job IDs
        app.MapPost(
            "/status",
            ([FromBody] StatusRequest request) =>
            {
                if (request.JobIds is not { Count: > 0 })
                    return Results.BadRequest(new { error = "jobIds list is required" });

                var results = request
                    .JobIds.Select(id =>
                    {
                        if (!Jobs.TryGetValue(id, out var job))
                            return (object)new { jobId = id, error = "Job not found" };

                        return (object)
                            new
                            {
                                jobId = job.Id,
                                url = job.Url,
                                status = job.Status.ToString().ToLowerInvariant(),
                                progress = Math.Round(job.Progress * 100, 1),
                                outputFilePath = job.OutputFilePath,
                                error = job.Error,
                            };
                    })
                    .ToList();

                return Results.Ok(new { jobs = results });
            }
        );

        // List all jobs
        app.MapGet(
            "/jobs",
            () =>
                Results.Ok(
                    Jobs.Values.Select(job => new
                    {
                        jobId = job.Id,
                        url = job.Url,
                        status = job.Status.ToString().ToLowerInvariant(),
                        progress = Math.Round(job.Progress * 100, 1),
                        outputFilePath = job.OutputFilePath,
                        error = job.Error,
                    })
                )
        );

        await app.RunAsync(cancellationToken);
    }

    private static async Task RunJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        await DownloadSemaphore.WaitAsync(cancellationToken);
        job.Status = JobStatus.Running;

        try
        {
            Directory.CreateDirectory(DownloadDirectory);

            using var resolver = new QueryResolver();
            using var downloader = new VideoDownloader();

            var result = await resolver.ResolveAsync(job.Url, cancellationToken);

            if (result.Videos.Count == 0)
                throw new InvalidOperationException("No videos found for the given URL.");

            var video = result.Videos[0];

            var preference = new VideoDownloadPreference(
                Container.Mp4,
                VideoQualityPreference.Highest
            );

            var option = await downloader.GetBestDownloadOptionAsync(
                video.Id,
                preference,
                cancellationToken: cancellationToken
            );

            var fileName = FileNameTemplate.Apply("$title", video, option.Container);
            var filePath = Path.Combine(DownloadDirectory, fileName);

            var progress = new Progress<Percentage>(p => job.Progress = p.Fraction);

            await downloader.DownloadVideoAsync(
                filePath,
                video,
                option,
                progress: progress,
                cancellationToken: cancellationToken
            );

            job.OutputFilePath = filePath;
            job.Progress = 1.0;
            job.Status = JobStatus.Completed;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = JobStatus.Failed;
        }
        finally
        {
            DownloadSemaphore.Release();
        }
    }

    private record DownloadRequest(List<string> Urls);

    private record StatusRequest(List<string> JobIds);
}
