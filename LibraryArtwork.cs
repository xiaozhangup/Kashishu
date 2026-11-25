using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kashishu;

/// <summary>Page-local cache shared by playlist covers, album thumbnails and the selected collection.</summary>
internal sealed class LibraryArtwork
{
    private sealed class Download(CancellationTokenSource cancellation, Task<Bitmap?> task)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<Bitmap?> Task { get; } = task;
        public int Readers;
    }

    private readonly Dictionary<string, Bitmap> _images = [];
    private readonly LinkedList<string> _recent = [];
    private readonly Dictionary<string, Download> _pending = [];
    private readonly SemaphoreSlim _transfers = new(6);
    public bool Active { get; private set; }
    public event Action? ActivityChanged;

    public void SetActive(bool active)
    {
        if (Active == active) return;
        Active = active;
        ActivityChanged?.Invoke();
    }

    public async Task<Bitmap?> ReadAsync(string url, CancellationToken token)
    {
        if (_images.TryGetValue(url, out var cached))
        {
            _recent.Remove(url);
            _recent.AddLast(url);
            return cached;
        }
        if (!_pending.TryGetValue(url, out var job) || job.Cancellation.IsCancellationRequested)
        {
            var cancellation = new CancellationTokenSource();
            job = new Download(cancellation, LoadAsync(url, cancellation.Token));
            _pending[url] = job;
        }
        job.Readers++;
        try
        {
            var image = await job.Task.WaitAsync(token);
            if (image is not null && _images.TryAdd(url, image))
            {
                _recent.AddLast(url);
                if (_recent.Count > 128)
                {
                    _images.Remove(_recent.First!.Value);
                    _recent.RemoveFirst();
                }
            }
            return image;
        }
        finally
        {
            if (--job.Readers == 0)
            {
                if (_pending.TryGetValue(url, out var current) && ReferenceEquals(current, job)) _pending.Remove(url);
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }
        }
    }

    private async Task<Bitmap?> LoadAsync(string url, CancellationToken token)
    {
        await _transfers.WaitAsync(token);
        try
        {
            var bytes = await SpotifyService.LoadImageSafeAsync(url, token);
            if (bytes.IsDefaultOrEmpty) return null;
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes.ToArray(), writable: false);
                try { return Bitmap.DecodeToWidth(stream, 240); }
                catch (ArgumentException) { return null; }
                catch (InvalidDataException) { return null; }
            }, token);
        }
        finally { _transfers.Release(); }
    }
}

/// <summary>Rounded cover tile with crossfading artwork layers and optional hover play overlay.</summary>
internal sealed class LibraryArtTile : Border
{
    private readonly LibraryArtwork _artwork;
    private readonly Image[] _layers = [Layer(), Layer()];
    private readonly PathIcon _symbol;
    private readonly Border _playOverlay;
    private readonly PathIcon _playIcon;
    private string? _url;
    private string? _displayed;
    private int _front;
    private CancellationTokenSource? _request;
    private bool _overlayVisible;

    private static Image Layer() => new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = 0,
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() }
        }
    };

    public LibraryArtTile(
        LibraryArtwork artwork,
        double size,
        string symbol,
        IBrush accent,
        string? url = null,
        double cornerRadius = 10,
        bool shadow = false)
    {
        _artwork = artwork;
        _url = url;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        CornerRadius = new CornerRadius(cornerRadius);
        Background = LyricsTheme.InsetBackground;
        if (shadow)
            BoxShadow = LyricsTheme.CardShadow;

        _symbol = LibraryIcons.Icon(symbol, Math.Max(14, size * .42), accent);
        _symbol.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }
        };

        _playIcon = LibraryIcons.Icon(LibraryIcons.Play, Math.Max(14, size * .42), Brushes.White);
        _playIcon.Margin = new Thickness(1.5, 0, 0, 0);
        _playOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(148, 8, 12, 18)),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = _playIcon,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() }
            }
        };

        var clipped = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            ClipToBounds = true,
            Child = new Grid
            {
                Children = { _symbol, _layers[0], _layers[1], _playOverlay }
            }
        };
        var rim = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        Child = new Grid { Children = { clipped, rim } };

        AttachedToVisualTree += (_, _) => { _artwork.ActivityChanged += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => { _artwork.ActivityChanged -= Refresh; Cancel(); };
    }

    public void Resize(double size, double? cornerRadius = null)
    {
        Width = Height = size;
        _symbol.Width = _symbol.Height = Math.Max(14, size * .42);
        if (cornerRadius is { } r)
        {
            var radius = new CornerRadius(r);
            CornerRadius = radius;
            if (Child is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is Border border)
                        border.CornerRadius = radius;
                }
            }
        }
    }

    public void SetPlayOverlay(bool visible, bool isPlaying)
    {
        _playIcon.Data = StreamGeometry.Parse(isPlaying ? LibraryIcons.Pause : LibraryIcons.Play);
        _playIcon.Margin = new Thickness(isPlaying ? 0 : 1.5, 0, 0, 0);
        if (_overlayVisible == visible) return;
        _overlayVisible = visible;
        _playOverlay.Opacity = visible ? 1 : 0;
    }

    public void SetCover(string? url, string symbol)
    {
        _symbol.Data = StreamGeometry.Parse(symbol);
        if (_url == url) return;
        _url = url;
        _displayed = null;
        _symbol.Opacity = 1;
        foreach (var layer in _layers)
            layer.Opacity = 0;
        Refresh();
    }

    private void Cancel()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
    }

    private void Refresh()
    {
        Cancel();
        if (!_artwork.Active || _url is not { } url || _displayed == url) return;
        _request = new();
        _ = ShowAsync(url, _request.Token);
    }

    private async Task ShowAsync(string url, CancellationToken token)
    {
        try
        {
            var image = await _artwork.ReadAsync(url, token);
            if (token.IsCancellationRequested || image is null) return;
            _layers[_front].Opacity = 0;
            _front = 1 - _front;
            _layers[_front].Source = image;
            _layers[_front].Opacity = 1;
            _symbol.Opacity = 0;
            _displayed = url;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

/// <summary>Compact three-bar equalizer indicator for the active track row.</summary>
internal sealed class LibraryAnimatedLevels : Grid
{
    private readonly Border[] _bars;
    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _active;
    private bool _playing;

    public LibraryAnimatedLevels()
    {
        Width = 14;
        Height = 14;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        ColumnDefinitions = new ColumnDefinitions("*,2,*,2,*");
        _bars = [MakeBar(0), MakeBar(2), MakeBar(4)];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(95) };
        _timer.Tick += (_, _) => Step();
        AttachedToVisualTree += (_, _) => UpdateTimer();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private Border MakeBar(int column)
    {
        var bar = new Border
        {
            Width = 3,
            Height = 5,
            CornerRadius = new CornerRadius(1.5),
            Background = LyricsTheme.Accent,
            VerticalAlignment = VerticalAlignment.Bottom,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(110), Easing = new CubicEaseOut() }
            }
        };
        SetColumn(bar, column);
        Children.Add(bar);
        return bar;
    }

    public void SetState(bool active, bool playing)
    {
        _active = active;
        _playing = playing;
        IsVisible = active;
        if (active && !playing)
        {
            _bars[0].Height = 6;
            _bars[1].Height = 10;
            _bars[2].Height = 5;
        }
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var shouldRun = _active && _playing && this.IsAttachedToVisualTree();
        if (shouldRun)
        {
            if (!_timer.IsEnabled)
            {
                Step();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Step()
    {
        _phase += 0.85;
        _bars[0].Height = 4 + 9 * (0.5 + 0.5 * Math.Sin(_phase));
        _bars[1].Height = 4 + 10 * (0.5 + 0.5 * Math.Sin(_phase + 1.9));
        _bars[2].Height = 4 + 8 * (0.5 + 0.5 * Math.Sin(_phase + 3.6));
    }
}
