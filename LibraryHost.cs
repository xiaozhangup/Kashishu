using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kashishu;

internal sealed class LibraryHost : Grid
{
    private readonly Func<Action, LibraryPage> _createPage;
    private readonly Action _onCloseRequested;
    private LibraryPage? _page;
    private int _visibilityChange;

    public LibraryHost(Func<Action, LibraryPage> createPage, Action onCloseRequested)
    {
        _createPage = createPage;
        _onCloseRequested = onCloseRequested;
        IsVisible = false;
        Background = Brushes.Transparent;
        Margin = new Thickness(22, 62, 22, 22);
        ClipToBounds = true;
        Focusable = true;
        TextElement.SetForeground(this, LyricsTheme.PrimaryText);
        TextElement.SetFontFamily(this, new FontFamily("Noto Sans CJK SC"));
        TextElement.SetFontSize(this, 14);
        PanelTheme.Apply(this);
    }

    public bool IsOpen { get; private set; }

    public void Open(int? slideFromDirection = null)
    {
        if (IsOpen)
            return;
        _visibilityChange++;
        IsOpen = IsVisible = IsHitTestVisible = true;
        if (_page is null)
        {
            _page = _createPage(_onCloseRequested);
            Children.Add(_page);
        }
        _page.OnShown();
        _page.AnimateIn(slideFromDirection);
        Focus();
    }

    public bool BackToCollections() => IsOpen && _page!.BackToCollections();

    public void Close(int? slideToDirection = null)
    {
        if (!IsOpen)
            return;
        IsOpen = IsHitTestVisible = false;
        _page!.OnHidden();
        _page.AnimateOut(slideToDirection);
        var change = ++_visibilityChange;
        DispatcherTimer.RunOnce(() =>
        {
            if (_visibilityChange == change)
                IsVisible = false;
        }, TimeSpan.FromMilliseconds(260));
    }
}
