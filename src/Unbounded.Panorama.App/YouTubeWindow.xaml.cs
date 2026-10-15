using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.App;

/// <summary>Paste a link → see the best resolution → pick minute and length → download that section.</summary>
public partial class YouTubeWindow : Window
{
    private readonly YouTubeImporter _importer;
    private readonly DispatcherTimer _frameDebounce;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _frameWork;
    private YouTubeVideo? _video;
    private bool _syncing;

    public ImportedClip? Result { get; private set; }
    public string? VideoTitle => _video?.Title;

    public YouTubeWindow(YouTubeImporter importer, string? initialUrl = null)
    {
        InitializeComponent();
        _importer = importer;
        _frameDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) =>
        {
            _frameDebounce!.Stop();
            await LoadFrameAsync();
        }, Dispatcher) { IsEnabled = false };

        Loaded += (_, _) =>
        {
            if (initialUrl is not null)
            {
                // Dropped link: fetch straight away.
                UrlBox.Text = initialUrl;
                OnFetch(this, new RoutedEventArgs());
                return;
            }
            // Pre-fill from the clipboard when it holds a YouTube link.
            try
            {
                var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                if (clip.Contains("youtu", StringComparison.OrdinalIgnoreCase))
                    UrlBox.Text = clip;
            }
            catch
            {
            }
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            _work?.Cancel();
            _frameWork?.Cancel();
        };
    }

    private double Start => StartSlider.Value;
    private double Length => LengthSlider.Value;

    private async void OnFetch(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
            return;
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        SetBusy(true, "Reading video information…");
        RangePanel.IsEnabled = false;
        ImportButton.IsEnabled = false;
        try
        {
            var video = await Task.Run(() => _importer.ProbeAsync(url, cts.Token), cts.Token);
            _video = video;
            TitleText.Text = video.Title;
            MetaText.Text = $"{video.Channel} · {YouTubeImporter.FormatTime(video.DurationSeconds)}";
            BestText.Text = "Best available: " + video.BestDescription
                + (video.OnlyHdrAvailable ? "  (HDR only — colours may look washed out)" : "");

            _syncing = true;
            StartSlider.Maximum = Math.Max(0, video.DurationSeconds - 1);
            StartSlider.Value = 0;
            LengthSlider.Maximum = Math.Clamp(video.DurationSeconds, 3, 300);
            LengthSlider.Value = Math.Min(30, LengthSlider.Maximum);
            _syncing = false;
            SyncBoxes();

            if (video.ThumbnailUrl is { } thumb)
                ShowImage(new Uri(thumb));
            RangePanel.IsEnabled = true;
            ImportButton.IsEnabled = true;
            SetBusy(false, "Pick the minute and length. The preview shows the exact first frame.");
            _ = LoadFrameAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetBusy(false, ex.Message);
        }
    }

    private async Task LoadFrameAsync()
    {
        if (_video is not { } video)
            return;
        _frameWork?.Cancel();
        var cts = _frameWork = new CancellationTokenSource();
        var at = Math.Round(Start, 1);
        StatusText.Text = $"Loading frame at {YouTubeImporter.FormatTime(at)}…";
        try
        {
            var png = await Task.Run(() => _importer.FrameAsync(video, at, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                ShowImage(new Uri(png));
                StatusText.Text = $"First frame at {YouTubeImporter.FormatTime(at)}.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                StatusText.Text = "Frame preview failed: " + ex.Message;
        }
    }

    private void ShowImage(Uri source)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = source;
        image.EndInit();
        Frame.Source = image;
        FrameHint.Visibility = Visibility.Collapsed;
    }

    private void OnStartSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
            return;
        SyncBoxes();
        _frameDebounce.Stop();
        _frameDebounce.Start();
    }

    private void OnLengthSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing)
            SyncBoxes();
    }

    private void SyncBoxes()
    {
        if (StartBox is null || LengthBox is null)
            return;
        StartBox.Text = YouTubeImporter.FormatTime(Math.Round(Start));
        LengthBox.Text = Math.Round(Length).ToString(CultureInfo.InvariantCulture);
    }

    private void OnStartBox(object sender, RoutedEventArgs e)
    {
        if (YouTubeImporter.TryParseTime(StartBox.Text, out var s))
            StartSlider.Value = Math.Clamp(s, 0, StartSlider.Maximum);
        SyncBoxes();
    }

    private void OnLengthBox(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(LengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
            LengthSlider.Value = Math.Clamp(l, LengthSlider.Minimum, LengthSlider.Maximum);
        SyncBoxes();
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender == StartBox)
            OnStartBox(sender, e);
        else
            OnLengthBox(sender, e);
        e.Handled = true;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_video is not { } video)
            return;
        _work?.Cancel();
        _frameWork?.Cancel();
        var cts = _work = new CancellationTokenSource();
        SetBusy(true, "Downloading…");
        RangePanel.IsEnabled = false;
        ImportButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            Result = await Task.Run(() => _importer.DownloadAsync(video, Start, Length, progress, cts.Token), cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetBusy(false, ex.Message);
            RangePanel.IsEnabled = true;
            ImportButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy, string text)
    {
        FetchButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        StatusText.Text = text;
        Cursor = busy ? Cursors.AppStarting : null;
    }
}
