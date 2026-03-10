using System;
using System.Collections.Concurrent;
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
    private const string DownloadDirectory = @"d:\downloads";
    private const int Port = 5005;

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

        // Start a download job
        app.MapPost(
            "/download",
            ([FromBody] DownloadRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.Url))
                    return Results.BadRequest(new { error = "url is required" });

                var job = new DownloadJob { Url = request.Url };
                Jobs[job.Id] = job;

                _ = Task.Run(() => RunJobAsync(job, cancellationToken), cancellationToken);

                return Results.Accepted(
                    $"/status/{job.Id}",
                    new { jobId = job.Id, statusUrl = $"/status/{job.Id}" }
                );
            }
        );

        // Check job status
        app.MapGet(
            "/status/{jobId}",
            (string jobId) =>
            {
                if (!Jobs.TryGetValue(jobId, out var job))
                    return Results.NotFound(new { error = "Job not found" });

                return Results.Ok(
                    new
                    {
                        jobId = job.Id,
                        url = job.Url,
                        status = job.Status.ToString().ToLowerInvariant(),
                        progress = Math.Round(job.Progress * 100, 1),
                        outputFilePath = job.OutputFilePath,
                        error = job.Error,
                    }
                );
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
    }

    private record DownloadRequest(string Url);
}
