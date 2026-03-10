# YoutubeDownloader — Local API Server

This is a fork of [YoutubeDownloader](https://github.com/Tyrrrz/YoutubeDownloader) extended with a **local REST API** so that an AI agent (via an MCP tool) can trigger YouTube downloads programmatically — without touching the GUI.

---

## What was added

A background HTTP server (`ApiServer.cs`) starts automatically when the app launches, exposing a minimal REST API on **`http://localhost:5005`**. The Avalonia UI continues to run normally alongside it.

The API reuses `YoutubeDownloader.Core` directly (the same engine the UI uses), so all existing download logic, FFmpeg integration, and YoutubeExplode are available.

---

## API Reference

### `GET /health`
Confirms the service is up.

**Response:**
```json
{
  "status": "ok",
  "service": "YoutubeDownloader API",
  "port": 5005
}
```

---

### `POST /download`
Queues a download job. Automatically selects the **highest resolution MP4** available and saves to `d:\downloads`.

**Request body:**
```json
{
  "url": "https://www.youtube.com/watch?v=..."
}
```

**Response (`202 Accepted`):**
```json
{
  "jobId": "17704ffc",
  "statusUrl": "/status/17704ffc"
}
```

The download runs asynchronously in the background. Use the `statusUrl` to poll for progress.

---

### `GET /status/{jobId}`
Returns the current state of a download job.

**Response:**
```json
{
  "jobId": "17704ffc",
  "url": "https://www.youtube.com/watch?v=...",
  "status": "completed",
  "progress": 100.0,
  "outputFilePath": "d:\\downloads\\Video Title.mp4",
  "error": null
}
```

**Status values:**
| Value | Meaning |
|-------|---------|
| `pending` | Job is queued, not yet started |
| `running` | Actively downloading |
| `completed` | Download finished successfully |
| `failed` | An error occurred (see `error` field) |

---

### `GET /jobs`
Lists all jobs (past and present) in the current session.

**Response:** array of job objects (same shape as `/status/{jobId}`).

---

## Download behaviour

- **Format:** Always MP4
- **Quality:** Always highest available (e.g. 4K/2160p if available)
- **Output folder:** `d:\downloads` (created automatically if it doesn't exist)
- **File name:** Derived from the video title, e.g. `Video Title.mp4`
- **Subtitles:** Embedded automatically when available
- **FFmpeg:** Bundled with the app, used automatically

---

## MCP Tool — integration guide

The MCP tool should be a Python service that wraps this API. Suggested implementation pattern:

```python
import httpx
import time

BASE_URL = "http://localhost:5005"

def download_youtube_video(url: str) -> str:
    """
    Downloads a YouTube video at the highest available MP4 quality.
    Blocks until the download completes and returns the local file path.
    """
    # Start the job
    r = httpx.post(f"{BASE_URL}/download", json={"url": url}, timeout=10)
    r.raise_for_status()
    job_id = r.json()["jobId"]

    # Poll until done
    while True:
        time.sleep(3)
        s = httpx.get(f"{BASE_URL}/status/{job_id}", timeout=10).json()
        status = s["status"]

        if status == "completed":
            return s["outputFilePath"]
        elif status == "failed":
            raise RuntimeError(f"Download failed: {s['error']}")
        # else: pending / running — keep polling
```

**MCP tool descriptor (suggested):**
- **Name:** `download_youtube_video`
- **Description:** Downloads a YouTube video (highest quality MP4) to the local machine and returns the file path. Accepts any YouTube URL (video, playlist, channel, or search query).
- **Input:** `url` (string) — the YouTube URL or search query
- **Output:** absolute file path of the downloaded `.mp4` file

---

## Prerequisites

To run this app (and therefore the API):

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [PowerShell 7+ (`pwsh`)](https://aka.ms/powershell) — required to auto-download FFmpeg at build time
- Windows, macOS, or Linux

### Build & run

```powershell
dotnet build YoutubeDownloader\YoutubeDownloader.csproj --configuration Debug
.\YoutubeDownloader\bin\Debug\net10.0\YoutubeDownloader.exe
```

Once running, the API is immediately available at `http://localhost:5005`.

---

## Architecture notes

- `ApiServer.cs` lives in `YoutubeDownloader/Api/`
- It starts on a `Task.Run` background thread before Avalonia initialises
- Uses ASP.NET Core minimal API (via `Microsoft.AspNetCore.App` framework reference)
- Jobs are held in a `ConcurrentDictionary` in memory — they do not persist across app restarts
- The API server shuts down cleanly when the app exits via a shared `CancellationTokenSource`
