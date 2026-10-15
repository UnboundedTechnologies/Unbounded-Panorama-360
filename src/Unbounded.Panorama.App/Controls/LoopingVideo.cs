using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Unbounded.Panorama.App.Controls;

/// <summary>
/// Plays a muted, looping video as a Border's background: a DrawingBrush with Stretch
/// UniformToFill crops evenly around the centre, like the ImageBrush thumbnails (a MediaElement
/// larger than its slot would be cropped from the left). Plays only while the border is visible
/// and inside its ScrollViewer's viewport (and the window is not minimised), so a long library does
/// not decode every card at once.
/// Usage: <c>controls:LoopingVideo.Source="{Binding Clip}"</c>.
/// </summary>
public static class LoopingVideo
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(Uri), typeof(LoopingVideo), new PropertyMetadata(null, OnSourceChanged));

    public static Uri? GetSource(DependencyObject d) => (Uri?)d.GetValue(SourceProperty);

    public static void SetSource(DependencyObject d, Uri? value) => d.SetValue(SourceProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Player), typeof(LoopingVideo));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;
        if (border.GetValue(StateProperty) is not Player state)
        {
            state = new Player(border);
            border.SetValue(StateProperty, state);
        }
        state.SetSource((Uri?)e.NewValue);
    }

    private sealed class Player
    {
        private readonly Border _border;
        private MediaPlayer? _player;
        private Uri? _source;
        private ScrollViewer? _scroller;
        private bool _playing;

        public Player(Border border)
        {
            _border = border;
            border.Loaded += (_, _) => Attach();
            border.Unloaded += (_, _) => Detach();
            border.IsVisibleChanged += (_, _) => Update();
            if (border.IsLoaded)
                Attach();
        }

        public void SetSource(Uri? source)
        {
            _source = source;
            Close();
            Update();
        }

        private Window? _window;

        private void OnWindowState(object? sender, EventArgs e) => Update();

        private void Attach()
        {
            if (_window is null && Window.GetWindow(_border) is { } window)
            {
                _window = window;
                _window.StateChanged += OnWindowState;
            }
            if (_scroller is null)
            {
                _scroller = FindScroller(_border);
                if (_scroller is not null)
                {
                    _scroller.ScrollChanged += OnScroll;
                    _scroller.SizeChanged += OnScrollerSize;
                }
            }
            Update();
        }

        private void Detach()
        {
            if (_window is not null)
            {
                _window.StateChanged -= OnWindowState;
                _window = null;
            }
            if (_scroller is not null)
            {
                _scroller.ScrollChanged -= OnScroll;
                _scroller.SizeChanged -= OnScrollerSize;
                _scroller = null;
            }
            Close();
        }

        private void OnScroll(object sender, ScrollChangedEventArgs e) => Update();

        private void OnScrollerSize(object sender, SizeChangedEventArgs e) => Update();

        private static ScrollViewer? FindScroller(DependencyObject d)
        {
            for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is ScrollViewer s)
                    return s;
            }
            return null;
        }

        private bool OnScreen()
        {
            if (!_border.IsLoaded || !_border.IsVisible || _border.ActualWidth <= 0 || _window?.WindowState == WindowState.Minimized)
                return false;
            if (_scroller is null)
                return true;
            try
            {
                var bounds = _border.TransformToAncestor(_scroller).TransformBounds(new Rect(_border.RenderSize));
                return bounds.IntersectsWith(new Rect(0, 0, _scroller.ViewportWidth, _scroller.ViewportHeight));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void Update()
        {
            var play = _source is not null && OnScreen();
            if (play == _playing && (_player is not null || !play))
                return;
            _playing = play;
            if (!play)
            {
                _player?.Pause();
                return;
            }
            if (_player is null)
            {
                var player = new MediaPlayer { IsMuted = true };
                player.MediaOpened += (_, _) =>
                {
                    // The brush appears once the size is known; until then the still thumbnail shows.
                    var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, Math.Max(1, player.NaturalVideoWidth), Math.Max(1, player.NaturalVideoHeight)) };
                    _border.Background = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
                };
                player.MediaEnded += (_, _) =>
                {
                    player.Position = TimeSpan.Zero;
                    if (_playing)
                        player.Play();
                };
                player.MediaFailed += (_, _) =>
                {
                    // Keep the still thumbnail; do not retry on every scroll.
                    _source = null;
                    Close();
                };
                player.Open(_source!);
                _player = player;
            }
            _player.Play();
        }

        private void Close()
        {
            _playing = false;
            if (_player is not null)
            {
                _player.Close();
                _player = null;
            }
            _border.Background = null;
        }
    }
}
