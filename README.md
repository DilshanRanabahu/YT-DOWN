# YT-DOWN

A Windows desktop app (C# / .NET 10 / WPF) for downloading YouTube and TikTok videos.
Downloads are handled by [yt-dlp](https://github.com/yt-dlp/yt-dlp); the app gives it a friendly UI.

## Features

- Paste or drag & drop a YouTube / TikTok link to preview the thumbnail, title, creator and duration
- Formats: Best, 1080p, 720p, 480p (MP4) or audio only (MP3)
- Live progress with overall percentage, size, speed and time remaining
- Automatic retries when TikTok's bot protection blocks a request (HTTP 403)
- Cancel anytime; partial downloads resume next time
- One-click update of the download engine

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build)
- **Recommended:** [ffmpeg](https://ffmpeg.org/) on `PATH`, needed for merging best-quality video + audio and for MP3
- **Recommended:** [Node.js](https://nodejs.org/) or [Deno](https://deno.com/) on `PATH`, which lets yt-dlp access all YouTube formats

`yt-dlp.exe` is downloaded automatically on first use to `%LOCALAPPDATA%\VideoDownloader`.

## Run

```
cd VideoDownloader
dotnet run
```

## Build a single .exe

```
cd VideoDownloader
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output is in `bin\Release\net10.0-windows\win-x64\publish\VideoDownloader.exe`.

## Troubleshooting

- **Download fails:** click **Update engine** in the app (YouTube and TikTok change often).
- **TikTok HTTP 403:** the app retries automatically; if all retries fail, wait a few minutes and try again.
- **YouTube is slow:** YouTube may throttle the download speed; choose 720p for a smaller file.

## Disclaimer

Only download content you own or have permission to download. Respect YouTube's and TikTok's terms of service and copyright law.
