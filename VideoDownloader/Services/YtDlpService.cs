using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Services;

public record VideoInfo(string Title, string? Uploader, string? Thumbnail, double? Duration, string Platform);

/// <param name="Percent">Overall progress (0-100) across all parts of the download.</param>
/// <param name="Stage">Human-readable step, e.g. "Downloading video (1/2)" or "Merging…".</param>
/// <param name="Downloaded">Bytes of the current part downloaded so far, if known.</param>
/// <param name="Total">Size of the current part in bytes (exact or estimated), if known.</param>
public record DownloadProgress(double Percent, string Speed, string Eta, string Stage,
    long? Downloaded = null, long? Total = null);

public enum QualityOption
{
    Best,
    P1080,
    P720,
    P480,
    AudioMp3
}

/// <summary>
/// Thin wrapper around the yt-dlp command-line tool, which does the actual
/// YouTube / TikTok extraction. yt-dlp.exe is downloaded automatically on first use.
/// </summary>
public sealed class YtDlpService
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    // yt-dlp pads the values with spaces, e.g. "PROGRESS|  5.0%|  1.2MiB/s|00:03|1048576|20971520"
    private static readonly Regex ProgressLine = new(
        @"^PROGRESS\|\s*(?<pct>[\d.]+)%?\s*\|(?<speed>[^|]*)\|(?<eta>[^|]*)(\|(?<done>[^|]*)\|(?<total>.*))?$");

    private const string ProgressTemplate =
        "download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s" +
        "|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s";

    private Process? _current;

    // "[info] abc123: Downloading 1 format(s): 399+140" — the '+' tells us video and audio come separately
    private static readonly Regex FormatsLine = new(@"Downloading \d+ format\(s\):\s*(?<ids>\S+)");

    public string ToolsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDownloader");

    public string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");

    public string? FfmpegPath { get; } = FindOnPath("ffmpeg.exe");

    public bool HasFfmpeg => FfmpegPath != null;

    /// <summary>
    /// yt-dlp needs a JavaScript runtime to unlock YouTube's regular formats; without one it only
    /// gets HLS streams, which download slower and report little or no progress.
    /// Value is in yt-dlp's "name:path" form, or null when none is installed.
    /// </summary>
    public string? JsRuntime { get; } =
        new[] { "deno", "node", "bun" }
            .Select(name => FindOnPath(name + ".exe") is { } path ? $"{name}:{path}" : null)
            .FirstOrDefault(r => r != null);

    public async Task EnsureYtDlpAsync(IProgress<string> log, CancellationToken ct = default)
    {
        if (File.Exists(YtDlpPath))
            return;

        Directory.CreateDirectory(ToolsDir);
        log.Report("Downloading yt-dlp (first run only)...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VideoDownloader/1.0");
        var tmp = YtDlpPath + ".part";
        await using (var src = await http.GetStreamAsync(YtDlpUrl, ct))
        await using (var dst = File.Create(tmp))
        {
            await src.CopyToAsync(dst, ct);
        }
        File.Move(tmp, YtDlpPath, overwrite: true);
        log.Report("yt-dlp installed.");
    }

    public async Task UpdateYtDlpAsync(IProgress<string> log, CancellationToken ct = default)
    {
        await EnsureYtDlpAsync(log, ct);
        await RunAsync(["-U"], log.Report, ct);
    }

    public async Task<VideoInfo> GetInfoAsync(string url, IProgress<string>? log = null, CancellationToken ct = default)
    {
        url = NormalizeUrl(url);
        var json = new StringBuilder();
        var errors = new StringBuilder();

        for (var attempt = 1; ; attempt++)
        {
            json.Clear();
            errors.Clear();
            var exit = await RunAsync(
                ["--dump-single-json", "--no-playlist", "--no-warnings", url],
                line => json.AppendLine(line), ct, err => errors.AppendLine(err));

            if (exit == 0)
                break;

            if (IsBlocked(errors.ToString()) && attempt < MaxAttempts)
            {
                log?.Report($"Request blocked (HTTP 403), retrying ({attempt + 1}/{MaxAttempts})...");
                await Task.Delay(RetryDelay(attempt), ct);
                continue;
            }

            throw new InvalidOperationException(LastErrorLine(errors.ToString()) ?? "Could not read video info.");
        }

        using var doc = JsonDocument.Parse(json.ToString());
        var root = doc.RootElement;

        return new VideoInfo(
            Title: GetString(root, "title") ?? "(untitled)",
            Uploader: GetString(root, "uploader") ?? GetString(root, "channel"),
            Thumbnail: PickThumbnail(root),
            Duration: root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
            Platform: GetString(root, "extractor_key") ?? "Unknown");
    }

    public async Task<int> DownloadAsync(
        string url,
        string outputDir,
        QualityOption quality,
        IProgress<DownloadProgress> progress,
        IProgress<string> log,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--windows-filenames",
            "--progress-template", ProgressTemplate,
            "-P", outputDir,
            "-o", "%(title).150B [%(id)s].%(ext)s",
        };

        if (HasFfmpeg)
            args.AddRange(["--ffmpeg-location", Path.GetDirectoryName(FfmpegPath)!]);

        args.AddRange(BuildFormatArgs(quality));
        args.Add(NormalizeUrl(url));

        for (var attempt = 1; ; attempt++)
        {
            var blocked = false;
            var parts = 1;   // number of streams yt-dlp will fetch (2 when video + audio are merged)
            var part = 0;    // which stream is currently downloading (1-based)

            var exit = await RunAsync(args, line =>
            {
                var text = line.Trim();
                var m = ProgressLine.Match(text);
                if (m.Success && double.TryParse(m.Groups["pct"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    var current = Math.Clamp(part, 1, parts);
                    var overall = ((current - 1) + pct / 100.0) / parts * 100.0;
                    var stage = parts == 1
                        ? "Downloading…"
                        : $"Downloading {(current == 1 ? "video" : "audio")} ({current}/{parts})";
                    progress.Report(new DownloadProgress(overall, m.Groups["speed"].Value.Trim(), m.Groups["eta"].Value.Trim(),
                        stage, ParseBytes(m.Groups["done"].Value), ParseBytes(m.Groups["total"].Value)));
                    return;
                }

                log.Report(line);

                if (FormatsLine.Match(text) is { Success: true } f)
                    parts = Math.Max(1, f.Groups["ids"].Value.Split('+').Length);
                else if (text.StartsWith("[download] Destination:"))
                    part++;
                else if (text.StartsWith("[Merger]"))
                    progress.Report(new DownloadProgress(100, "", "", "Merging video and audio…"));
                else if (text.StartsWith("[ExtractAudio]"))
                    progress.Report(new DownloadProgress(100, "", "", "Converting to MP3…"));
                else if (text.Contains("has already been downloaded"))
                    progress.Report(new DownloadProgress(100, "", "", "Already downloaded"));
            }, ct, err =>
            {
                blocked |= IsBlocked(err);
                log.Report(err);
            });

            if (exit == 0 || !blocked || attempt >= MaxAttempts)
                return exit;

            log.Report($"Request blocked (HTTP 403), retrying ({attempt + 1}/{MaxAttempts})...");
            await Task.Delay(RetryDelay(attempt), ct);
        }
    }

    /// <summary>Kills the running yt-dlp process (and its ffmpeg children), e.g. when the app closes.</summary>
    public void KillCurrent()
    {
        try { _current?.Kill(entireProcessTree: true); } catch { /* already exited */ }
    }

    private static long? ParseBytes(string s) =>
        double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)v : null;

    // TikTok's bot protection randomly rejects requests with HTTP 403;
    // the same request usually succeeds on a later attempt.
    private const int MaxAttempts = 6;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(2 * attempt, 8));

    private static bool IsBlocked(string stderr) =>
        stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips tracking query parameters (?is_from_webapp=... etc.) from TikTok links.</summary>
    private static string NormalizeUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/video/"))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }
        return url;
    }

    private IEnumerable<string> BuildFormatArgs(QualityOption quality)
    {
        // Without ffmpeg yt-dlp cannot merge separate video/audio streams,
        // so fall back to the best single file that already contains both.
        if (!HasFfmpeg)
        {
            return quality switch
            {
                QualityOption.AudioMp3 => ["-f", "ba[ext=m4a]/ba/b"],
                QualityOption.P1080 => ["-f", "b[height<=1080]/b"],
                QualityOption.P720 => ["-f", "b[height<=720]/b"],
                QualityOption.P480 => ["-f", "b[height<=480]/b"],
                _ => ["-f", "b"],
            };
        }

        return quality switch
        {
            QualityOption.AudioMp3 => ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"],
            QualityOption.P1080 => VideoFormat(1080),
            QualityOption.P720 => VideoFormat(720),
            QualityOption.P480 => VideoFormat(480),
            _ => ["-f", "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b", "--merge-output-format", "mp4"],
        };

        static string[] VideoFormat(int h) =>
        [
            "-f", $"bv*[height<={h}][ext=mp4]+ba[ext=m4a]/bv*[height<={h}]+ba/b[height<={h}]/b",
            "--merge-output-format", "mp4"
        ];
    }

    private async Task<int> RunAsync(
        IEnumerable<string> args,
        Action<string> onOutput,
        CancellationToken ct,
        Action<string>? onError = null)
    {
        var psi = new ProcessStartInfo(YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        if (JsRuntime != null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add(JsRuntime);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) (onError ?? onOutput)(e.Data); };

        proc.Start();
        _current = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
        finally
        {
            if (_current == proc) _current = null;
        }

        return proc.ExitCode;
    }

    /// <summary>
    /// WPF often can't decode WebP, which YouTube prefers, so use YouTube's JPEG thumbnail instead.
    /// </summary>
    private static string? PickThumbnail(JsonElement root)
    {
        if (GetString(root, "extractor_key") == "Youtube" && GetString(root, "id") is { } id)
            return $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        return GetString(root, "thumbnail");
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? LastErrorLine(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));

    private static string? FindOnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            try
            {
                var full = Path.Combine(p.Trim(), exe);
                if (File.Exists(full))
                    return full;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return null;
    }
}
