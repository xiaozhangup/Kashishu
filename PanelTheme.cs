using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Kashishu;

/// <summary>Gives stock Avalonia controls the lyrics-window look inside the panel host.</summary>
internal static class PanelTheme
{
    public static void Apply(Control host)
    {
        RegisterThemeResources(host);
        RegisterButtonStyles(host);
        SmoothScroll.Attach(host);
    }

    private static Setter ColourFade() => new(Animatable.TransitionsProperty, new Transitions
    {
        new BrushTransition
        {
            Property = ContentPresenter.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        },
        new BrushTransition
        {
            Property = ContentPresenter.BorderBrushProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        },
        new BrushTransition
        {
            Property = ContentPresenter.ForegroundProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        }
    });

    private static void RegisterThemeResources(Control host)
    {
        // Fluent looks these brushes up by key, so the stock controls pick up the lyrics palette.
        IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));
        IBrush White(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        void Set(IBrush brush, params string[] keys)
        {
            foreach (var key in keys)
                host.Resources[key] = brush;
        }

        Set(LyricsTheme.Accent, "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed");
        Set(White(0x26), "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffPressed");
        Set(White(0x55), "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOffPressed");
        Set(LyricsTheme.OnAccent, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Set(LyricsTheme.PrimaryText, "ToggleSwitchKnobFillOff", "ToggleSwitchKnobFillOffPointerOver", "ToggleSwitchKnobFillOffPressed");
        Set(White(0x30), "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed");
        Set(LyricsTheme.Accent, "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed");
        Set(Solid("#F2F5F2"), "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed");
        Set(LyricsTheme.InputBackground, "ComboBoxBackground", "TextControlBackground");
        Set(LyricsTheme.InputHoverBackground, "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
            "ComboBoxBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused");
        Set(LyricsTheme.CardBorder, "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        Set(LyricsTheme.Accent, "TextControlBorderBrushFocused", "ComboBoxBorderBrushFocused");
        Set(LyricsTheme.DropDownBackground, "ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground");
        Set(White(0x28), "ComboBoxDropDownBorderBrush", "MenuFlyoutPresenterBorderBrush");
    }

    private static void RegisterButtonStyles(Control host)
    {
        // Fluent restyles the button template on hover/press, so state colours and transitions live on the presenter.
        void State(string cls, string pseudo, IBrush background, IBrush? foreground = null, IBrush? border = null)
        {
            var style = new Style(x => x.OfType<Button>().Class(cls).Class(pseudo)
                .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, background) }
            };
            if (foreground is not null)
                style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
            if (border is not null)
                style.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, border));
            host.Styles.Add(style);
        }

        void Rest(string cls, IBrush background, IBrush? foreground = null, IBrush? border = null)
        {
            var style = new Style(x => x.OfType<Button>().Class(cls)
                .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, background), ColourFade() }
            };
            if (foreground is not null)
                style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
            if (border is not null)
                style.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, border));
            host.Styles.Add(style);
        }

        var transparentWhite = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
        var hover = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
        var pressed = new SolidColorBrush(Color.FromArgb(0x34, 255, 255, 255));
        Rest("chrome", transparentWhite, LyricsTheme.PrimaryText);
        State("chrome", ":pointerover", hover, LyricsTheme.PrimaryText);
        State("chrome", ":pressed", pressed, LyricsTheme.PrimaryText);

        var closeHover = new SolidColorBrush(Color.FromArgb(175, 228, 76, 91));
        var closePressed = new SolidColorBrush(Color.FromArgb(215, 228, 76, 91));
        Rest("chromeclose", transparentWhite, LyricsTheme.PrimaryText);
        State("chromeclose", ":pointerover", closeHover, Brushes.White);
        State("chromeclose", ":pressed", closePressed, Brushes.White);

        var accentHover = LyricsTheme.AccentHover;
        var accentPressed = LyricsTheme.AccentPressed;
        Rest("accent", LyricsTheme.Accent, LyricsTheme.OnAccent);
        State("accent", ":pointerover", accentHover, LyricsTheme.OnAccent);
        State("accent", ":pressed", accentPressed, LyricsTheme.OnAccent);

        Rest("glass", LyricsTheme.InputBackground, LyricsTheme.PrimaryText, LyricsTheme.CardBorder);
        State("glass", ":pointerover", LyricsTheme.InputHoverBackground, LyricsTheme.PrimaryText, LyricsTheme.CardBorder);
        State("glass", ":pressed", pressed, LyricsTheme.PrimaryText, LyricsTheme.CardBorder);

        // Segmented selector.
        Rest("seg", transparentWhite, LyricsTheme.PrimaryText);
        State("seg", ":pointerover", transparentWhite, LyricsTheme.PrimaryText);
        State("seg", ":pressed", transparentWhite, LyricsTheme.PrimaryText);
        Rest("segactive", transparentWhite, LyricsTheme.OnAccent);
        State("segactive", ":pointerover", transparentWhite, LyricsTheme.OnAccent);
        State("segactive", ":pressed", transparentWhite, LyricsTheme.OnAccent);

        // Sidebar and row entries: transparent until hovered, tinted with the cover accent while current.
        Rest("nav", transparentWhite, LyricsTheme.PrimaryText);
        State("nav", ":pointerover", new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)), LyricsTheme.PrimaryText);
        State("nav", ":pressed", pressed, LyricsTheme.PrimaryText);
        var navActive = LyricsTheme.NavActive;
        Rest("navactive", navActive, LyricsTheme.PrimaryText);
        State("navactive", ":pointerover", navActive, LyricsTheme.PrimaryText);
        State("navactive", ":pressed", navActive, LyricsTheme.PrimaryText);
    }
}

/// <summary>Replaces discrete wheel jumps on ScrollViewers with frame-synchronized exponential smoothing.</summary>
internal sealed class SmoothScroll : IDisposable
{
    private readonly Control _host;
    private readonly FrameScheduler _scheduler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ScrollViewer? _scroller;
    private double _targetY;
    private double _currentY;
    private double _lastTimestamp;
    private bool _animating;
    private bool _queued;

    private SmoothScroll(Control host)
    {
        _host = host;
        host.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerPressedEvent, (_, _) => Stop(), RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        host.DetachedFromVisualTree += (_, _) => Dispose();
    }

    public static void Attach(Control host) => _ = new SmoothScroll(host);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            Stop();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Math.Abs(e.Delta.Y) < 0.001)
            return;
        if (FindScrollableViewer(e.Source as Visual) is not { } scroller)
            return;

        var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        if (maxY <= 0)
            return;

        e.Handled = true;
        var sameTarget = ReferenceEquals(_scroller, scroller) && _animating;
        if (!sameTarget)
        {
            _scroller = scroller;
            _currentY = _targetY = scroller.Offset.Y;
            _lastTimestamp = _clock.Elapsed.TotalSeconds;
        }

        _targetY = Math.Clamp(_targetY - e.Delta.Y * 108, 0, maxY);
        _animating = true;
        QueueFrame();
    }

    private static ScrollViewer? FindScrollableViewer(Visual? start)
    {
        for (var current = start; current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollViewer scroller
                && scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
                && scroller.Extent.Height > scroller.Viewport.Height + 0.5)
                return scroller;
        }
        return null;
    }

    private void QueueFrame()
    {
        if (_queued || !_animating || _scroller is null || TopLevel.GetTopLevel(_host) is not { } top)
            return;
        _queued = true;
        _scheduler.Request(top, _ =>
        {
            _queued = false;
            Step();
        });
    }

    private void Step()
    {
        if (!_animating || _scroller is not { } scroller || !scroller.IsAttachedToVisualTree() || !scroller.IsEffectivelyVisible)
        {
            Stop();
            return;
        }

        // Stop if another interaction (scrollbar drag, programmatic jump) moved the viewport.
        if (Math.Abs(scroller.Offset.Y - _currentY) > 3.5)
        {
            Stop();
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastTimestamp, 1d / 240, 0.05);
        _lastTimestamp = now;

        var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        _targetY = Math.Clamp(_targetY, 0, maxY);

        var remaining = _targetY - _currentY;
        if (Math.Abs(remaining) <= 0.35)
        {
            scroller.Offset = new Vector(scroller.Offset.X, _targetY);
            Stop();
            return;
        }

        var factor = 1 - Math.Exp(-17 * dt);
        _currentY += remaining * factor;
        scroller.Offset = new Vector(scroller.Offset.X, _currentY);
        QueueFrame();
    }

    private void Stop()
    {
        _animating = false;
        _scroller = null;
    }

    public void Dispose()
    {
        Stop();
        _scheduler.Dispose();
    }
}
