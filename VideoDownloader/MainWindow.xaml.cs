using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoDownloader.Services;

namespace VideoDownloader;

public partial class MainWindow : Window
{
    private readonly YtDlpService _ytdlp = new();
    private readonly IProgress<string> _log;
    private CancellationTokenSource? _cts;
    private string _folder;

    public MainWindow()
    {
        InitializeComponent();
        _log = new Progress<string>(AppendLog);

        _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        FolderText.Text = _folder;

        if (!_ytdlp.HasFfmpeg)
            AppendLog("ffmpeg not found on PATH: downloads will use single-file formats and MP3 conversion is unavailable.");
        else
            AppendLog($"Using ffmpeg: {_ytdlp.FfmpegPath}");

        if (_ytdlp.JsRuntime == null)
            AppendLog("No JavaScript runtime (deno/node/bun) found: YouTube will offer fewer formats. Install Deno or Node.js for best results.");
        else
            AppendLog($"Using JS runtime: {_ytdlp.JsRuntime}");
    }

    // ---------- Input ----------

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        UrlBox.Text = Clipboard.GetText().Trim();
        Fetch_Click(sender, e);
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Fetch_Click(sender, e);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.Text) is string text && FetchButton.IsEnabled)
        {
            UrlBox.Text = text.Trim();
            Fetch_Click(sender, e);
        }
    }

    // ---------- Actions ----------

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var url = GetValidUrl();
        if (url == null) return;

        SetBusy(true, "Fetching video info…");
        ShowEmptyState("", "Loading preview…");
        try
        {
            await _ytdlp.EnsureYtDlpAsync(_log);
            var info = await _ytdlp.GetInfoAsync(url, _log);
            ShowInfo(info);
            StatusText.Text = "Ready to download";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't load this video — see details";
            ShowEmptyState("", "Couldn't load a preview for this link");
            AppendLog(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = GetValidUrl();
        if (url == null) return;

        var quality = QualityPanel.Children.OfType<RadioButton>()
            .Where(r => r.IsChecked == true)
            .Select(r => Enum.Parse<QualityOption>((string)r.Tag))
            .FirstOrDefault();
        Directory.CreateDirectory(_folder);

        _cts = new CancellationTokenSource();
        SetBusy(true, "Starting download…");
        ResetProgress();

        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Value = p.Percent;
            PercentText.Text = $"{p.Percent:0}%";
            StatusText.Text = p.Downloaded is long done && p.Total is long total && total > 0
                ? $"{p.Stage}  ·  {FormatSize(done)} of {FormatSize(total)}"
                : p.Stage;
            SpeedText.Text = string.IsNullOrWhiteSpace(p.Speed) || p.Speed.Contains("N/A") ? "—" : p.Speed;
            EtaText.Text = string.IsNullOrWhiteSpace(p.Eta) || p.Eta.Contains("N/A") ? "—" : p.Eta;
        });

        try
        {
            await _ytdlp.EnsureYtDlpAsync(_log, _cts.Token);
            var exit = await _ytdlp.DownloadAsync(url, _folder, quality, progress, _log, _cts.Token);
            if (exit == 0)
            {
                Progress.Value = 100;
                PercentText.Text = "100%";
                PercentText.Foreground = (Brush)FindResource("Success");
                StatusText.Text = "Download complete — saved to your folder";
                EtaText.Text = "done";
            }
            else
            {
                StatusText.Text = "Download failed — open details for more info";
                LogToggle.IsChecked = true;
            }
        }
        catch (OperationCanceledException)
        {
            ResetProgress();
            StatusText.Text = "Download cancelled";
            AppendLog("Download cancelled. The partial file was kept and will resume if you download this video again.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Download failed — open details for more info";
            LogToggle.IsChecked = true;
            AppendLog(ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts == null) return;

        var answer = MessageBox.Show(this,
            "A download is still in progress. Stop it and quit?\n\nThe partial file is kept and will resume next time.",
            "Download in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // The async cancel path may not get to run once the window is gone, so kill yt-dlp directly.
        _ytdlp.KillCurrent();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = _folder };
        if (dlg.ShowDialog(this) == true)
        {
            _folder = dlg.FolderName;
            FolderText.Text = _folder;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Updating download engine…");
        try
        {
            await _ytdlp.UpdateYtDlpAsync(_log);
            StatusText.Text = "Download engine is up to date";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Update failed — see details";
            AppendLog(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- UI helpers ----------

    private string? GetValidUrl()
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "Please paste a valid YouTube or TikTok link";
            UrlBox.Focus();
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!(host.EndsWith("youtube.com") || host.EndsWith("youtu.be") || host.EndsWith("tiktok.com")))
            AppendLog($"Note: {host} is not YouTube/TikTok — trying anyway.");

        return url;
    }

    private void ShowEmptyState(string glyph, string message)
    {
        EmptyIcon.Text = glyph;
        EmptyText.Text = message;
        PreviewEmpty.Visibility = Visibility.Visible;
        PreviewContent.Visibility = Visibility.Collapsed;
    }

    private void ShowInfo(VideoInfo info)
    {
        PreviewEmpty.Visibility = Visibility.Collapsed;
        PreviewContent.Visibility = Visibility.Visible;

        TitleText.Text = info.Title;
        UploaderText.Text = info.Uploader ?? "Unknown creator";

        var (label, brush) = info.Platform switch
        {
            "Youtube" => ("YOUTUBE", (Brush)new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33))),
            "TikTok" => ("TIKTOK", new LinearGradientBrush(
                Color.FromRgb(0x25, 0xF4, 0xEE), Color.FromRgb(0xFE, 0x2C, 0x55), 0)),
            _ => (info.Platform.ToUpperInvariant(), (Brush)FindResource("Violet")),
        };
        PlatformText.Text = label;
        PlatformBadge.Background = brush;

        if (info.Duration is double secs)
        {
            DurationText.Text = TimeSpan.FromSeconds(secs).ToString(secs >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            DurationBadge.Visibility = Visibility.Visible;
        }
        else
        {
            DurationBadge.Visibility = Visibility.Collapsed;
        }

        ThumbBorder.Background = null;
        if (Uri.TryCreate(info.Thumbnail, UriKind.Absolute, out var thumb))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = thumb;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                ThumbBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { /* unsupported thumbnail format — keep the gradient placeholder */ }
        }
    }

    private void ResetProgress()
    {
        Progress.Value = 0;
        PercentText.Text = "0%";
        PercentText.ClearValue(ForegroundProperty);
        SpeedText.Text = "—";
        EtaText.Text = "—";
    }

    private void SetBusy(bool busy, string? status = null)
    {
        PasteButton.IsEnabled = !busy;
        FetchButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        QualityPanel.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && _cts != null;
        if (status != null) StatusText.Text = status;
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ---------- Dark title bar (Windows 10 20H1+ / Windows 11) ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x001A0F0E;                                        // #0E0F1A as COLORREF (0x00BBGGRR)
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));       // DWMWA_CAPTION_COLOR (Win11)
    }
}
