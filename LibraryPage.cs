using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kashishu;

internal sealed class LibraryPage : UserControl
{
    private enum LibraryTab
    {
        Collections,
        Recent,
        Albums,
        Search
    }

    private sealed record CollectionItem(
        string? Id,
        string Name,
        string? Url,
        string? ImageUrl = null,
        string? Owner = null,
        int? TrackCount = null,
        string? Description = null,
        bool Collaborative = false);

    private static readonly CollectionItem SavedTracksCollection = new(
        null,
        "已收藏的歌曲",
        "https://open.spotify.com/collection/tracks",
        Description: "你在 Spotify 中收藏的所有单曲");

    private readonly PlaybackStore _store;
    private readonly Func<ISpotifyLibrary?> _getLibrary;
    private readonly Action _openSettings;
    private readonly Func<PlaybackCommand, CancellationToken, Task<string?>> _controlPlayback;
    private readonly Action<string> _showToast;
    private readonly Action _closeLibrary;

    private readonly LibraryArtwork _artwork = new();
    private readonly LibraryQueue _queue = new();
    private readonly Dictionary<string, bool> _favorites = new(StringComparer.Ordinal);
    private readonly List<TrackRowView> _attachedTrackRows = [];
    private readonly IReadOnlyList<Control> _entrance;
    private readonly Grid _topBar;
    private readonly Grid _centerHost;

    // Top bar
    private readonly Segmented<LibraryTab> _tabs = new("音乐库导航");
    private readonly Border _statusDot = new()
    {
        Width = 7,
        Height = 7,
        CornerRadius = new CornerRadius(3.5),
        Background = LyricsTheme.MutedText,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _statusText = Ui.Text("", 12, muted: true);
    private readonly Border _statusPill;
    private readonly Button _settingsButton = Ui.Button("前往设置");
    private readonly Button _refreshButton;
    private readonly Button _backToLyrics = Ui.Button("返回歌词");

    // Tab views
    private readonly Grid _tabHost = new();
    private readonly Control _collectionsView;
    private readonly Control _recentView;
    private readonly Control _albumsView;
    private readonly Control _searchView;

    // Collections tab
    private readonly Grid _collectionsBody = new() { ColumnDefinitions = new ColumnDefinitions("224,12,*") };
    private readonly Border _collectionSidebarCard;
    private readonly TextBox _collectionFilter = CreateSearchBox("筛选歌单…");
    private readonly Button _collectionFilterClear = ClearInputButton();
    private readonly ListBox _collectionList = CreateStyledListBox();
    private readonly ObservableCollection<CollectionItem> _visibleCollections = [];
    private readonly List<CollectionItem> _allCollections = [SavedTracksCollection];
    private readonly TextBlock _collectionMessage = Ui.Text("", 12, muted: true);
    private readonly Button _retryCollections = Ui.Button("重试");
    private readonly LibraryTrackPane _collectionTrackPane;

    // Recent tab
    private readonly LibraryTrackPane _recentTrackPane;

    // Albums tab
    private readonly Grid _albumsBody = new() { ColumnDefinitions = new ColumnDefinitions("224,12,*") };
    private readonly Border _albumSidebarCard;
    private readonly TextBox _albumFilter = CreateSearchBox("筛选专辑…");
    private readonly Button _albumFilterClear = ClearInputButton();
    private readonly ListBox _albumList = CreateStyledListBox();
    private readonly ObservableCollection<LibraryAlbum> _visibleAlbums = [];
    private readonly List<LibraryAlbum> _allAlbums = [];
    private readonly TextBlock _albumMessage = Ui.Text("", 12, muted: true);
    private readonly Button _retryAlbums = Ui.Button("重试");
    private readonly LibraryTrackPane _albumTrackPane;

    // Search tab
    private readonly TextBox _searchBox = CreateSearchBox("搜索 Spotify 歌曲、歌手、专辑或歌单…");
    private readonly Button _searchClear = ClearInputButton();
    private readonly Button _searchSubmit = Ui.Button("搜索", accent: true);
    private readonly Grid _searchContentHost = new();
    private readonly Control _searchResultsView;
    private readonly StackPanel _searchEmptyState;
    private readonly StackPanel _searchResultsBody;
    private readonly StackPanel _searchCollectionsSection;
    private readonly UniformGrid _searchCollectionsGrid = new() { Margin = new Thickness(0, 0, -10, -10) };
    private readonly StackPanel _searchTracksSection;
    private readonly TextBlock _searchMessage = Ui.Text("", 12, muted: true);
    private readonly LibraryTrackPane _searchTracksPane;
    private readonly LibraryTrackPane _searchDetailPane;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(420) };

    // Setup / disconnected overlay card
    private readonly Border _setupCard;
    private readonly TextBlock _setupTitle = Ui.Text("尚未连接到 Spotify 资料库", 19, FontWeight.SemiBold);
    private readonly TextBlock _setupDescription = Ui.Text("", 13, muted: true);
    private readonly Button _setupAction = Ui.Button("前往设置", accent: true);
    private readonly Button _setupRetry = Ui.Button("重试");

    // Bottom floating dock
    private readonly Grid _miniBar;
    private readonly Border _dockSurface;
    private Color? _dockColor;
    private readonly LibraryArtTile _miniCover;
    private readonly TextBlock _miniTitle = Ui.Text("选择歌曲开始播放", 13, FontWeight.SemiBold);
    private readonly TextBlock _miniSubtitle = Ui.Text("", 11.5, muted: true);
    private readonly Button _miniPrev;
    private readonly Button _miniPlayPause;
    private readonly PathIcon _miniPlayIcon;
    private readonly Button _miniNext;
    private readonly Button _miniShuffle;
    private readonly Button _miniStopQueue;
    private readonly Button _dockPlayAll = Ui.Button("播放全部", accent: true);
    private readonly Button _dockShuffle = Ui.Button("随机");
    private readonly Button _dockOpenSpotify = Ui.Button("在 Spotify 打开");

    // State
    private ISpotifyLibrary? _library;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _session = new();
    private CancellationTokenSource? _collectionTracksRequest;
    private CancellationTokenSource? _albumTracksRequest;
    private CancellationTokenSource? _recentRequest;
    private CancellationTokenSource? _searchRequest;
    private LibraryTab _activeTab = LibraryTab.Collections;
    private CollectionItem _selectedCollection = SavedTracksCollection;
    private LibraryAlbum? _selectedAlbum;
    private int? _collectionOffset;
    private int? _albumOffset;
    private bool _loadingCollections;
    private bool _loadingAlbums;
    private bool _collectionsFailed;
    private bool _albumsFailed;
    private bool _recentLoaded;
    private bool _albumsInitialized;
    private bool _needsSpotifySettings;
    private bool _viewCollectionTracks;
    private bool _viewAlbumTracks;
    private bool _inSearchDetail;
    private bool _pageCheckQueued;
    private string? _currentTrackId;
    private bool _currentIsPlaying;
    private bool _miniBarVisible;
    private string _lastSearchQuery = string.Empty;
    private int? _selectedCollectionTotal;

    public IReadOnlyList<Control> EntranceTargets => _entrance;

    public LibraryPage(
        PlaybackStore store,
        Func<ISpotifyLibrary?> getLibrary,
        Action openSettings,
        Func<PlaybackCommand, CancellationToken, Task<string?>> controlPlayback,
        Action<string> showToast,
        Action closeLibrary)
    {
        _store = store;
        _getLibrary = getLibrary;
        _openSettings = openSettings;
        _controlPlayback = controlPlayback;
        _showToast = showToast;
        _closeLibrary = closeLibrary;

        // ── Top Navigation Bar (matches PanelHost) ─────────────────────────────
        _tabs.HorizontalAlignment = HorizontalAlignment.Left;
        _tabs.Set(TabChoices(), tab => tab == _activeTab);
        _tabs.Changed += SwitchTab;

        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusPill = new Border
        {
            Background = LyricsTheme.InsetBackground,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(LyricsTheme.PillRadius),
            Padding = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { _statusDot, _statusText }
            }
        };

        _refreshButton = IconAction("刷新资料库", LibraryIcons.Refresh);
        _refreshButton.Click += (_, _) => RefreshActiveView();
        _settingsButton.Click += (_, _) => _openSettings();
        _backToLyrics.Click += (_, _) => _closeLibrary();

        var topRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusPill, _settingsButton, _refreshButton, _backToLyrics }
        };
        _topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(14, 2, 14, 6),
            Children = { _tabs, topRight }
        };
        Grid.SetColumn(topRight, 2);

        // ── Collections Tab ────────────────────────────────────────────────────
        _collectionTrackPane = CreateTrackPane(
            backLabel: "返回歌单",
            onBack: () => BackToCollections(),
            onLoadMore: LoadCollectionTracksAsync);

        _collectionList.ItemsSource = _visibleCollections;
        _collectionList.Padding = new Thickness(0, 0, 0, 72);
        _collectionList.ItemTemplate = new FuncDataTemplate<CollectionItem>((item, _) =>
            item is null ? null : CollectionRow(item));
        _collectionList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is ListBoxItem && _collectionList.SelectedItem is CollectionItem item)
            {
                SelectCollection(item);
                e.Handled = true;
            }
        };
        _collectionList.LayoutUpdated += (_, _) =>
        {
            if (_collectionOffset.HasValue && !_collectionsFailed && !_loadingCollections && NearEnd(_collectionList))
                QueuePageCheck();
        };
        _collectionList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => QueuePageCheck());
        _retryCollections.Click += async (_, _) => await LoadCollectionsAsync();
        BindFilterBox(_collectionFilter, _collectionFilterClear, ApplyCollectionFilter);

        _collectionSidebarCard = BuildSidebarCard(
            _collectionFilter,
            _collectionFilterClear,
            _collectionList,
            _collectionMessage,
            _retryCollections);

        _collectionsBody.Children.Add(_collectionSidebarCard);
        _collectionsBody.Children.Add(_collectionTrackPane);
        Grid.SetColumn(_collectionTrackPane, 2);
        _collectionsView = new Border
        {
            Margin = new Thickness(14, 0, 14, 6),
            Child = _collectionsBody
        };

        // ── Recent Tab ─────────────────────────────────────────────────────────
        _recentTrackPane = CreateTrackPane(
            backLabel: null,
            onBack: null,
            onLoadMore: LoadRecentAsync);
        _recentTrackPane.ConfigureTitle("最近播放", null);
        _recentView = new Border
        {
            Margin = new Thickness(14, 0, 14, 6),
            IsVisible = false,
            Child = _recentTrackPane
        };

        // ── Albums Tab ─────────────────────────────────────────────────────────
        _albumTrackPane = CreateTrackPane(
            backLabel: "返回专辑",
            onBack: () => BackToCollections(),
            onLoadMore: LoadAlbumTracksAsync);

        _albumList.ItemsSource = _visibleAlbums;
        _albumList.Padding = new Thickness(0, 0, 0, 72);
        _albumList.ItemTemplate = new FuncDataTemplate<LibraryAlbum>((item, _) =>
            item is null ? null : AlbumRow(item));
        _albumList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is ListBoxItem && _albumList.SelectedItem is LibraryAlbum album)
            {
                SelectAlbum(album);
                e.Handled = true;
            }
        };
        _albumList.LayoutUpdated += (_, _) =>
        {
            if (_albumOffset.HasValue && !_albumsFailed && !_loadingAlbums && NearEnd(_albumList))
                QueuePageCheck();
        };
        _albumList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => QueuePageCheck());
        _retryAlbums.Click += async (_, _) => await LoadAlbumsAsync();
        BindFilterBox(_albumFilter, _albumFilterClear, ApplyAlbumFilter);

        _albumSidebarCard = BuildSidebarCard(
            _albumFilter,
            _albumFilterClear,
            _albumList,
            _albumMessage,
            _retryAlbums);

        _albumsBody.Children.Add(_albumSidebarCard);
        _albumsBody.Children.Add(_albumTrackPane);
        Grid.SetColumn(_albumTrackPane, 2);
        _albumsView = new Border
        {
            Margin = new Thickness(14, 0, 14, 6),
            IsVisible = false,
            Child = _albumsBody
        };

        // ── Search Tab ─────────────────────────────────────────────────────────
        _searchTracksPane = CreateTrackPane(
            backLabel: null,
            onBack: null,
            onLoadMore: null,
            compactHeader: true);

        _searchDetailPane = CreateTrackPane(
            backLabel: "返回搜索结果",
            onBack: ExitSearchDetail,
            onLoadMore: LoadSearchDetailMoreAsync);
        _searchDetailPane.IsVisible = false;

        _searchSubmit.Click += (_, _) => TriggerSearchNow();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TriggerSearchNow();
                e.Handled = true;
            }
        };
        _searchBox.TextChanged += (_, _) =>
        {
            var hasText = !string.IsNullOrWhiteSpace(_searchBox.Text);
            _searchClear.IsVisible = hasText;
            _searchDebounce.Stop();
            if (hasText)
                _searchDebounce.Start();
            else
                ResetSearchResults();
        };
        _searchClear.Click += (_, _) =>
        {
            _searchBox.Text = string.Empty;
            _searchDebounce.Stop();
            ResetSearchResults();
            _searchBox.Focus();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            TriggerSearchNow();
        };

        var searchInputBar = BuildSearchInputBar();
        _searchEmptyState = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 48),
            Children =
            {
                LibraryIcons.Icon(LibraryIcons.Search, 32, LyricsTheme.Accent),
                Ui.Text("探索 Spotify 曲库", 19, FontWeight.SemiBold),
                Ui.Text("输入歌名、歌手、专辑或歌单关键词，即可直接试听与收藏。", 13, muted: true)
            }
        };
        foreach (var child in _searchEmptyState.Children.OfType<TextBlock>())
            child.TextAlignment = TextAlignment.Center;

        _searchCollectionsSection = new StackPanel
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                Ui.Text("专辑与歌单", 16, FontWeight.SemiBold),
                _searchCollectionsGrid
            }
        };
        _searchTracksSection = new StackPanel
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                Ui.Text("相关歌曲", 16, FontWeight.SemiBold),
                _searchTracksPane
            }
        };
        _searchMessage.IsVisible = false;
        _searchResultsBody = new StackPanel
        {
            Spacing = 20,
            Margin = new Thickness(0, 0, 10, 66),
            Children = { _searchMessage, _searchCollectionsSection, _searchTracksSection }
        };
        var searchScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, -10, 0),
            Content = new Grid { Children = { _searchEmptyState, _searchResultsBody } }
        };
        _searchResultsView = searchScroller;
        _searchContentHost.Children.Add(_searchResultsView);
        _searchContentHost.Children.Add(_searchDetailPane);

        var searchGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,10,*"),
            Margin = new Thickness(14, 0, 14, 6),
            Children = { searchInputBar, _searchContentHost }
        };
        Grid.SetRow(_searchContentHost, 2);
        _searchView = searchGrid;
        _searchView.IsVisible = false;

        _tabHost.Children.Add(_collectionsView);
        _tabHost.Children.Add(_recentView);
        _tabHost.Children.Add(_albumsView);
        _tabHost.Children.Add(_searchView);

        // ── Setup / Attention Card ─────────────────────────────────────────────
        _setupAction.Click += (_, _) => _openSettings();
        _setupRetry.Click += (_, _) => BindLibrary(force: true);
        _setupCard = Ui.Card(new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 420,
            Children =
            {
                LibraryIcons.Icon(LibraryIcons.Playlist, 34, LyricsTheme.Accent),
                _setupTitle,
                _setupDescription,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { _setupAction, _setupRetry }
                }
            }
        }, padding: 28);
        _setupCard.HorizontalAlignment = HorizontalAlignment.Center;
        _setupCard.VerticalAlignment = VerticalAlignment.Center;
        _setupCard.IsVisible = false;

        // ── Floating Bottom Blur Dock ──────────────────────────────────────────
        _miniCover = new LibraryArtTile(_artwork, 36, LibraryIcons.Note, LyricsTheme.Accent, cornerRadius: LyricsTheme.ControlRadius);
        _miniTitle.TextWrapping = TextWrapping.NoWrap;
        _miniTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _miniTitle.FontSize = 13;
        _miniSubtitle.TextWrapping = TextWrapping.NoWrap;
        _miniSubtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _miniSubtitle.FontSize = 11.5;
        _miniPrev = CompactIconButton("上一首", LibraryIcons.Previous, 15);
        _miniPrev.Width = _miniPrev.Height = 34;
        _miniPrev.CornerRadius = new CornerRadius(17);
        _miniPlayIcon = LibraryIcons.Icon(LibraryIcons.Play, 15, LyricsTheme.OnAccent);
        _miniPlayIcon.Margin = new Thickness(1.5, 0, 0, 0);
        _miniPlayPause = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = LyricsTheme.Accent,
            Foreground = LyricsTheme.OnAccent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = _miniPlayIcon,
            Classes = { "accent" }
        };
        AutomationProperties.SetName(_miniPlayPause, "播放或暂停");
        _miniNext = CompactIconButton("下一首", LibraryIcons.Next, 15);
        _miniNext.Width = _miniNext.Height = 34;
        _miniNext.CornerRadius = new CornerRadius(17);
        _miniShuffle = CompactIconButton("随机队列", LibraryIcons.Shuffle, 14);
        _miniShuffle.Width = _miniShuffle.Height = 32;
        _miniShuffle.CornerRadius = new CornerRadius(16);
        _miniStopQueue = CompactIconButton("结束连播", LibraryIcons.Close, 13);
        _miniStopQueue.Width = _miniStopQueue.Height = 30;
        _miniStopQueue.CornerRadius = new CornerRadius(15);

        _miniPrev.Click += async (_, _) => await HandlePreviousAsync();
        _miniPlayPause.Click += async (_, _) => await HandlePlayPauseAsync();
        _miniNext.Click += async (_, _) => await HandleNextAsync();
        _miniShuffle.Click += (_, _) =>
        {
            _queue.ToggleShuffle();
            _showToast(_queue.IsShuffled ? "已切换为随机连播" : "已切换为顺序连播");
        };
        _miniStopQueue.Click += (_, _) => _queue.Stop();

        _dockPlayAll.Height = 34;
        _dockPlayAll.Padding = new Thickness(14, 0);
        _dockPlayAll.CornerRadius = new CornerRadius(LyricsTheme.PillRadius);
        _dockPlayAll.FontSize = 12.5;
        _dockPlayAll.Background = LyricsTheme.InputBackground;
        _dockPlayAll.Foreground = LyricsTheme.PrimaryText;
        _dockPlayAll.BorderThickness = new Thickness(1);
        _dockPlayAll.Classes.Set("accent", false);
        _dockPlayAll.Classes.Set("glass", true);
        _dockPlayAll.Content = DockButtonLabel(LibraryIcons.Play, "播放全部", LyricsTheme.PrimaryText);
        _dockPlayAll.Click += async (_, _) =>
        {
            var pane = ActiveTrackPane();
            await PlayFromListAsync(pane.VisibleTracks, null, shuffle: false, ActiveContextUri(pane));
        };

        _dockShuffle.Height = 34;
        _dockShuffle.Padding = new Thickness(12, 0);
        _dockShuffle.CornerRadius = new CornerRadius(LyricsTheme.PillRadius);
        _dockShuffle.FontSize = 12.5;
        _dockShuffle.Content = DockButtonLabel(LibraryIcons.Shuffle, "随机", LyricsTheme.PrimaryText);
        _dockShuffle.Click += async (_, _) =>
        {
            var pane = ActiveTrackPane();
            await PlayFromListAsync(pane.VisibleTracks, null, shuffle: true, ActiveContextUri(pane));
        };

        _dockOpenSpotify.Width = 34;
        _dockOpenSpotify.Height = 34;
        _dockOpenSpotify.Padding = new Thickness(0);
        _dockOpenSpotify.CornerRadius = new CornerRadius(17);
        _dockOpenSpotify.HorizontalContentAlignment = HorizontalAlignment.Center;
        _dockOpenSpotify.Content = LibraryIcons.Icon(LibraryIcons.ExternalLink, 15, LyricsTheme.PrimaryText);
        ToolTip.SetTip(_dockOpenSpotify, "在 Spotify 打开");
        AutomationProperties.SetName(_dockOpenSpotify, "在 Spotify 打开");
        _dockOpenSpotify.Click += async (_, _) => await OpenSpotifyAsync(ActiveSpotifyUrl());

        var miniInfo = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _miniTitle, _miniSubtitle }
        };
        var transportControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _miniShuffle, _miniPrev, _miniPlayPause, _miniNext, _miniStopQueue }
        };
        var listActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dockPlayAll, _dockShuffle, _dockOpenSpotify }
        };
        var leftTrackGroup = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,10,*"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _miniCover, miniInfo }
        };
        Grid.SetColumn(miniInfo, 2);

        var dockContent = new Grid
        {
            Margin = new Thickness(12, 8),
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = new ColumnDefinitions("*,12,Auto,12,*"),
            Children = { leftTrackGroup, transportControls, listActions }
        };
        Grid.SetColumn(transportControls, 2);
        Grid.SetColumn(listActions, 4);

        _centerHost = new Grid { Children = { _tabHost, _setupCard } };
        var dockGlassSheen = new Border
        {
            CornerRadius = new CornerRadius(LyricsTheme.CardRadius),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)),
            IsHitTestVisible = false
        };

        _dockSurface = new Border
        {
            Background = new SolidColorBrush(LyricsTheme.DockBackground.Color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(LyricsTheme.CardRadius),
            BoxShadow = LyricsTheme.CardShadow,
            Child = new Grid
            {
                Children = { dockGlassSheen, dockContent }
            }
        };

        _miniBar = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 840,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 14, 14),
            IsVisible = false,
            Children = { _dockSurface }
        };

        // ── Page Root Layout ───────────────────────────────────────────────────
        var bodyHost = new Grid
        {
            Children =
            {
                _centerHost,
                _miniBar
            }
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { _topBar, bodyHost }
        };
        Grid.SetRow(bodyHost, 1);
        Content = root;

        _entrance = [_topBar, _tabHost];
        _queue.Changed += () => Dispatcher.UIThread.Post(UpdateMiniBar);
        SizeChanged += (_, _) => UpdateLayoutMode();
        AttachedToVisualTree += (_, _) =>
        {
            if (_lifetime.IsCancellationRequested)
            {
                _lifetime.Dispose();
                _lifetime = new();
            }
            _store.Changed += OnStoreChanged;
            LyricsTheme.DockBackground.PropertyChanged += OnDockPaletteChanged;
            SyncDockPalette(animate: false);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            LyricsTheme.DockBackground.PropertyChanged -= OnDockPaletteChanged;
            _searchDebounce.Stop();
            _lifetime.Cancel();
            OnHidden();
        };
    }

    private static (LibraryTab Tab, string Label)[] TabChoices(bool compact = false) => compact
        ? [
            (LibraryTab.Collections, "歌单"),
            (LibraryTab.Recent, "最近"),
            (LibraryTab.Albums, "专辑"),
            (LibraryTab.Search, "搜索")
        ]
        : [
            (LibraryTab.Collections, "歌单与收藏"),
            (LibraryTab.Recent, "最近播放"),
            (LibraryTab.Albums, "我的专辑"),
            (LibraryTab.Search, "搜索曲库")
        ];

    private Border BuildSearchInputBar()
    {
        var icon = LibraryIcons.Icon(LibraryIcons.Search, 16, LyricsTheme.MutedText);
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.IsHitTestVisible = false;

        _searchBox.Padding = new Thickness(36, 9, 34, 9);
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchClear.HorizontalAlignment = HorizontalAlignment.Right;
        _searchClear.Margin = new Thickness(0, 0, 6, 0);

        var inputGrid = new Grid { Children = { _searchBox, icon, _searchClear } };
        icon.HorizontalAlignment = HorizontalAlignment.Left;

        var barGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,10,Auto"),
            Children = { inputGrid, _searchSubmit }
        };
        Grid.SetColumn(_searchSubmit, 2);
        _searchSubmit.Height = 40;
        var card = Ui.Card(barGrid, padding: 12);
        card.CornerRadius = new CornerRadius(16);
        return card;
    }

    private Border BuildSidebarCard(
        TextBox filterBox,
        Button clearButton,
        ListBox list,
        TextBlock message,
        Button retry)
    {
        filterBox.MinHeight = 32;
        filterBox.Padding = new Thickness(30, 5, 26, 5);
        filterBox.FontSize = 12.5;
        var searchGlyph = LibraryIcons.Icon(LibraryIcons.Search, 13.5, LyricsTheme.MutedText);
        searchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        searchGlyph.Margin = new Thickness(10, 0, 0, 0);
        searchGlyph.IsHitTestVisible = false;
        clearButton.HorizontalAlignment = HorizontalAlignment.Right;
        clearButton.Margin = new Thickness(0, 0, 4, 0);
        var filterWrap = new Grid { Children = { filterBox, searchGlyph, clearButton } };

        message.IsVisible = false;
        message.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
                message.IsVisible = !string.IsNullOrEmpty(message.Text);
        };
        retry.IsVisible = false;
        retry.HorizontalAlignment = HorizontalAlignment.Left;

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,8,*,Auto,Auto"),
            Children = { filterWrap, list, message, retry }
        };
        list.Margin = new Thickness(0, 0, -6, 0);
        Grid.SetRow(list, 2);
        Grid.SetRow(message, 3);
        Grid.SetRow(retry, 4);

        var card = Ui.Card(grid, padding: 10);
        card.CornerRadius = new CornerRadius(LyricsTheme.CardRadius);
        return card;
    }

    private LibraryTrackPane CreateTrackPane(
        string? backLabel,
        Action? onBack,
        Func<Task>? onLoadMore,
        bool compactHeader = false)
    {
        return new LibraryTrackPane(
            _artwork,
            backLabel,
            onBack,
            onLoadMore,
            UpdateMiniBar,
            track => HandleTrackRowClickAsync(track),
            (track, list) => PlayQueue(ActiveListTitle(), list, track, shuffle: false),
            ToggleTrackFavoriteAsync,
            OpenAlbumFromTrack,
            OpenSpotifyAsync,
            CopyTrackLinkAsync,
            CopyTrackInfoAsync,
            IsCurrentTrack,
            () => _currentIsPlaying,
            IsTrackFavorite,
            RegisterTrackRow,
            UnregisterTrackRow,
            compactHeader);
    }

    public void OnShown()
    {
        _artwork.SetActive(true);
        if (_session.IsCancellationRequested)
        {
            _session.Dispose();
            _session = new();
        }
        BindLibrary();
        UpdatePlaybackState(_store.Snapshot, allowQueueAdvance: false);
        SyncDockPalette(animate: false);
        UpdateLayoutMode();
    }

    public void AnimateIn(int? slideFromDirection)
    {
        var dx = slideFromDirection is { } slide ? 30 * slide : 0;
        var dy = slideFromDirection.HasValue ? 0 : 22;
        Motion.In(_topBar, dx, dy, TimeSpan.FromMilliseconds(340));
        Motion.In(_centerHost, dx, dy, TimeSpan.FromMilliseconds(380), delayMs: 35);
        if (_miniBarVisible)
            SlideDock(dx, dy, 0, 0, TimeSpan.FromMilliseconds(340), new CubicEaseOut());
    }

    public void AnimateOut(int? slideToDirection)
    {
        var dx = slideToDirection is { } slide ? 30 * slide : 0;
        var dy = slideToDirection.HasValue ? 0 : 20;
        Motion.Out(_topBar, dx, dy);
        Motion.Out(_centerHost, dx, dy);
        if (_miniBarVisible)
        {
            // Keep Opacity = 1 on the dock ancestors so FrostedDockBackdrop never renders inside SaveLayerAlpha.
            _miniBar.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = Motion.Leave,
                    Easing = new CubicEaseIn()
                }
            };
            _miniBar.RenderTransform = TransformOperations.Parse($"translate({dx}px, {(slideToDirection.HasValue ? 0 : 72)}px)");
        }
    }

    private void SlideDock(int fromX, int fromY, int toX, int toY, TimeSpan duration, Easing easing)
    {
        _miniBar.Transitions = null;
        _miniBar.RenderTransform = TransformOperations.Parse($"translate({fromX}px, {fromY}px)");
        Dispatcher.UIThread.Post(() =>
        {
            _miniBar.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = duration,
                    Easing = easing
                }
            };
            _miniBar.RenderTransform = TransformOperations.Parse($"translate({toX}px, {toY}px)");
        }, DispatcherPriority.Render);
    }

    public void OnHidden()
    {
        _artwork.SetActive(false);
        _searchDebounce.Stop();
        _session.Cancel();
    }

    public bool BackToCollections()
    {
        if (_activeTab == LibraryTab.Search && _inSearchDetail)
        {
            ExitSearchDetail();
            return true;
        }
        if (_activeTab == LibraryTab.Collections && _collectionTrackPane.IsBackVisible && _viewCollectionTracks)
        {
            _viewCollectionTracks = false;
            UpdateLayoutMode();
            Motion.In(_collectionSidebarCard, -12, 0);
            _collectionList.Focus();
            return true;
        }
        if (_activeTab == LibraryTab.Albums && _albumTrackPane.IsBackVisible && _viewAlbumTracks)
        {
            _viewAlbumTracks = false;
            UpdateLayoutMode();
            Motion.In(_albumSidebarCard, -12, 0);
            _albumList.Focus();
            return true;
        }
        return false;
    }

    private void SwitchTab(LibraryTab next)
    {
        if (_activeTab == next)
            return;
        var direction = next > _activeTab ? 1 : -1;
        _activeTab = next;
        _tabs.Set(TabChoices(Bounds.Width > 0 && Bounds.Width < 740), tab => tab == next);

        _collectionsView.IsVisible = next == LibraryTab.Collections;
        _recentView.IsVisible = next == LibraryTab.Recent;
        _albumsView.IsVisible = next == LibraryTab.Albums;
        _searchView.IsVisible = next == LibraryTab.Search;

        var activeView = ActiveTabControl(next);
        UpdateLayoutMode();
        Motion.In(activeView, 26 * direction, 0);
        UpdateMiniBar();

        if (_library is not null)
        {
            if (next == LibraryTab.Recent && !_recentLoaded)
                _ = LoadRecentAsync();
            else if (next == LibraryTab.Albums && !_albumsInitialized)
            {
                _albumsInitialized = true;
                _ = LoadAlbumsAsync();
            }
            else if (next == LibraryTab.Search)
            {
                Dispatcher.UIThread.Post(() => _searchBox.Focus(), DispatcherPriority.Loaded);
            }
        }
        QueuePageCheck();
    }

    private Control ActiveTabControl(LibraryTab tab) => tab switch
    {
        LibraryTab.Collections => _collectionsView,
        LibraryTab.Recent => _recentView,
        LibraryTab.Albums => _albumsView,
        LibraryTab.Search => _searchView,
        _ => _collectionsView
    };

    private void RefreshActiveView()
    {
        switch (_activeTab)
        {
            case LibraryTab.Collections:
                BindLibrary(force: true);
                break;
            case LibraryTab.Recent:
                _recentLoaded = false;
                _ = LoadRecentAsync();
                break;
            case LibraryTab.Albums:
                _allAlbums.Clear();
                _visibleAlbums.Clear();
                _albumOffset = 0;
                _albumsFailed = false;
                _albumMessage.Text = "";
                _retryAlbums.IsVisible = false;
                _ = LoadAlbumsAsync();
                break;
            case LibraryTab.Search:
                if (!string.IsNullOrWhiteSpace(_searchBox.Text))
                    TriggerSearchNow(force: true);
                break;
        }
    }

    private void UpdateLayoutMode()
    {
        var narrow = Bounds.Width > 0 && Bounds.Width < 760;
        var compactHeader = Bounds.Width > 0 && Bounds.Width < 740;
        _tabs.Set(TabChoices(compactHeader), tab => tab == _activeTab);
        _statusText.IsVisible = !compactHeader;
        ToolTip.SetTip(_statusPill, compactHeader ? _statusText.Text : null);

        // Collections split
        _collectionsBody.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(224);
        _collectionsBody.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(12);
        _collectionsBody.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_collectionTrackPane, narrow ? 0 : 2);
        _collectionSidebarCard.IsVisible = !narrow || !_viewCollectionTracks;
        _collectionTrackPane.IsVisible = !narrow || _viewCollectionTracks;
        _collectionTrackPane.SetCompactMode(narrow, showBack: narrow);

        // Albums split
        _albumsBody.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(224);
        _albumsBody.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(12);
        _albumsBody.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_albumTrackPane, narrow ? 0 : 2);
        _albumSidebarCard.IsVisible = !narrow || !_viewAlbumTracks;
        _albumTrackPane.IsVisible = !narrow || _viewAlbumTracks;
        _albumTrackPane.SetCompactMode(narrow, showBack: narrow);

        // Recent & Search
        _recentTrackPane.SetCompactMode(narrow, showBack: false);
        _searchDetailPane.SetCompactMode(narrow, showBack: true);

        var searchWidth = Bounds.Width > 0 ? Math.Max(240, Bounds.Width - 40) : 960;
        _searchCollectionsGrid.Columns = Math.Clamp((int)Math.Floor((searchWidth + 10) / 236), 1, 6);

        UpdateMiniBar();
        QueuePageCheck();
    }

    private void BindLibrary(bool force = false)
    {
        var next = _getLibrary();
        var status = next?.Status;
        UpdateConnectionHeader(next, status);

        if (!force && ReferenceEquals(next, _library) && _allCollections.Count > 0)
        {
            UpdateSetupCard();
            return;
        }

        _session.Cancel();
        _session.Dispose();
        _session = new();
        _library = next;
        _loadingCollections = false;
        _loadingAlbums = false;
        _collectionsFailed = false;
        _albumsFailed = false;
        _needsSpotifySettings = false;
        _recentLoaded = false;
        _albumsInitialized = false;

        _allCollections.Clear();
        _allCollections.Add(SavedTracksCollection);
        ApplyCollectionFilter();
        _collectionOffset = 0;
        _collectionMessage.Text = "";
        _retryCollections.IsVisible = false;

        _allAlbums.Clear();
        _visibleAlbums.Clear();
        _albumOffset = 0;
        _albumMessage.Text = "";
        _retryAlbums.IsVisible = false;

        UpdateSetupCard();
        SelectCollection(SavedTracksCollection, showTracks: false);
        if (next is not null)
        {
            _ = LoadCollectionsAsync();
            if (_activeTab == LibraryTab.Recent)
                _ = LoadRecentAsync();
            else if (_activeTab == LibraryTab.Albums)
            {
                _albumsInitialized = true;
                _ = LoadAlbumsAsync();
            }
        }
    }

    private void UpdateConnectionHeader(ISpotifyLibrary? library, string? status)
    {
        var connected = status == "Soloist 已连接";
        var needsSettings = library is null || _needsSpotifySettings || (status?.Contains("设置", StringComparison.Ordinal) ?? false);
        _settingsButton.IsVisible = needsSettings;

        if (status is null || connected)
        {
            _statusPill.IsVisible = false;
        }
        else
        {
            _statusPill.IsVisible = true;
            _statusDot.Background = LyricsTheme.Warning;
            _statusText.Text = status;
            _statusText.Foreground = LyricsTheme.PrimaryText;
        }
    }

    private void UpdateSetupCard()
    {
        var showSetup = _library is null || _needsSpotifySettings;
        _setupCard.IsVisible = showSetup;
        _tabHost.IsVisible = !showSetup;
        if (!showSetup) return;

        if (_library is null)
        {
            _setupTitle.Text = "Soloist 尚未就绪";
            _setupDescription.Text = "请先在设置中切换到 Soloist 并填写 API Key，然后等待与 Spotify 客户端完成配对。";
            _setupAction.IsVisible = true;
            _setupRetry.IsVisible = true;
        }
        else
        {
            _setupTitle.Text = "需要设置 Spotify 应用参数";
            _setupDescription.Text = "音乐库复用 Spotify Web API 的应用参数与账号授权。请在设置中切换到 Spotify 填写并保存参数后再切回 Soloist。";
            _setupAction.IsVisible = true;
            _setupRetry.IsVisible = true;
        }
    }

    private bool ShowSettingsFor(string message)
    {
        if (!message.Contains("设置", StringComparison.Ordinal))
            return false;
        _needsSpotifySettings = true;
        UpdateConnectionHeader(_library, _library?.Status);
        UpdateSetupCard();
        return true;
    }

    private void SelectCollection(CollectionItem item, bool showTracks = true)
    {
        _viewCollectionTracks = showTracks;
        UpdateLayoutMode();
        if (showTracks && _collectionTrackPane.IsVisible)
            Motion.In(_collectionTrackPane, _collectionTrackPane.IsBackVisible ? 16 : 0, _collectionTrackPane.IsBackVisible ? 0 : 8);

        if (showTracks && _selectedCollection == item && _collectionTrackPane.TotalLoaded > 0)
            return;

        _collectionTracksRequest?.Cancel();
        _collectionTracksRequest?.Dispose();
        _collectionTracksRequest = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _selectedCollection = item;
        _selectedCollectionTotal = item.TrackCount;
        _collectionList.SelectedItem = item;

        _collectionTrackPane.ConfigureTitle(item.Name, item.TrackCount);
        _collectionTrackPane.ResetTracks();
        UpdateMiniBar();

        if (_library is not null)
            _ = LoadCollectionTracksAsync();
    }

    private void SelectAlbum(LibraryAlbum album, bool showTracks = true)
    {
        _viewAlbumTracks = showTracks;
        UpdateLayoutMode();
        if (showTracks && _albumTrackPane.IsVisible)
            Motion.In(_albumTrackPane, _albumTrackPane.IsBackVisible ? 16 : 0, _albumTrackPane.IsBackVisible ? 0 : 8);

        if (showTracks && _selectedAlbum?.Id == album.Id && _albumTrackPane.TotalLoaded > 0)
            return;

        _albumTracksRequest?.Cancel();
        _albumTracksRequest?.Dispose();
        _albumTracksRequest = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _selectedAlbum = album;
        _albumList.SelectedItem = album;

        _albumTrackPane.ConfigureTitle(album.Name, album.TrackCount > 0 ? album.TrackCount : null);
        _albumTrackPane.ResetTracks();
        UpdateMiniBar();

        if (_library is not null)
            _ = LoadAlbumTracksAsync();
    }

    private async Task LoadCollectionsAsync()
    {
        if (_loadingCollections || _library is not { } library || _collectionOffset is not int offset)
            return;
        var token = _session.Token;
        _loadingCollections = true;
        _collectionsFailed = false;
        _retryCollections.IsVisible = false;
        var progress = ShowLoading(_collectionMessage, "正在读取歌单…", offset, token);
        try
        {
            var page = await library.ReadPlaylistsAsync(offset, token);
            if (token.IsCancellationRequested) return;
            foreach (var item in page.Items)
            {
                _allCollections.Add(new CollectionItem(
                    item.Id,
                    item.Name,
                    item.SpotifyUrl,
                    item.ImageUrl,
                    item.Owner,
                    item.TrackCount,
                    item.Description,
                    item.Collaborative));
            }
            ApplyCollectionFilter(animateFirstPage: offset == 0);
            _collectionOffset = page.NextOffset > offset ? page.NextOffset : null;
            _collectionMessage.Text = _allCollections.Count == 1 && _collectionOffset is null
                ? "没有找到更多可浏览的歌单"
                : "";
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _collectionMessage.Text = ex.Message;
            _collectionsFailed = true;
            _retryCollections.IsVisible = !ShowSettingsFor(ex.Message);
        }
        finally
        {
            progress?.Dispose();
            _loadingCollections = false;
        }
        QueuePageCheck();
    }

    private async Task LoadCollectionTracksAsync()
    {
        if (_collectionTrackPane.IsLoading || _library is not { } library || _collectionTrackPane.NextOffset is not int offset)
            return;
        var token = (_collectionTracksRequest ?? _session).Token;
        var currentCollection = _selectedCollection;
        _collectionTrackPane.BeginLoad(offset, "正在读取歌曲…", token);
        try
        {
            var page = await library.ReadTracksAsync(currentCollection.Id, offset, token);
            if (token.IsCancellationRequested || currentCollection != _selectedCollection) return;

            if (currentCollection.Id is null)
            {
                foreach (var track in page.Items)
                {
                    if (!string.IsNullOrEmpty(track.Uri))
                        _favorites[track.Uri] = true;
                }
            }

            _collectionTrackPane.AppendPage(page.Items, page.NextOffset, offset, "这里还没有歌曲");
            if (page.Total is int total && _selectedCollectionTotal != total)
            {
                _selectedCollectionTotal = total;
                _collectionTrackPane.ConfigureTitle(currentCollection.Name, total);
            }
            if (currentCollection.Id is not null)
                _ = PrefetchFavoritesAsync(page.Items, token);
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _collectionTrackPane.SetError(ex.Message, canRetry: !ShowSettingsFor(ex.Message));
        }
        finally
        {
            _collectionTrackPane.EndLoad();
        }
        QueuePageCheck();
    }

    private async Task LoadRecentAsync()
    {
        if (_recentTrackPane.IsLoading || _library is not { } library)
            return;
        _recentRequest?.Cancel();
        _recentRequest?.Dispose();
        _recentRequest = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        var token = _recentRequest.Token;
        _recentTrackPane.ResetTracks();
        _recentTrackPane.BeginLoad(0, "正在读取最近播放…", token);
        try
        {
            var page = await library.ReadRecentlyPlayedAsync(token);
            if (token.IsCancellationRequested) return;
            _recentLoaded = true;
            _recentTrackPane.ConfigureTitle("最近播放", page.Items.Count);
            _recentTrackPane.AppendPage(page.Items, null, 0, "最近还没有播放记录");
            _ = PrefetchFavoritesAsync(page.Items, token);
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _recentTrackPane.SetError(ex.Message, canRetry: !ShowSettingsFor(ex.Message));
        }
        finally
        {
            _recentTrackPane.EndLoad();
        }
    }

    private async Task LoadAlbumsAsync()
    {
        if (_loadingAlbums || _library is not { } library || _albumOffset is not int offset)
            return;
        var token = _session.Token;
        _loadingAlbums = true;
        _albumsFailed = false;
        _retryAlbums.IsVisible = false;
        var progress = ShowLoading(_albumMessage, "正在读取收藏专辑…", offset, token);
        try
        {
            var page = await library.ReadAlbumsAsync(offset, token);
            if (token.IsCancellationRequested) return;
            _allAlbums.AddRange(page.Items);
            ApplyAlbumFilter(animateFirstPage: offset == 0);
            _albumOffset = page.NextOffset > offset ? page.NextOffset : null;
            _albumMessage.Text = _allAlbums.Count == 0 && _albumOffset is null ? "还没有收藏任何专辑" : "";
            if (_selectedAlbum is null && _allAlbums.Count > 0)
                SelectAlbum(_allAlbums[0], showTracks: false);
            else if (_allAlbums.Count == 0 && _albumOffset is null)
                _albumTrackPane.ConfigureTitle("暂无收藏专辑", 0);
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _albumMessage.Text = ex.Message;
            _albumsFailed = true;
            _retryAlbums.IsVisible = !ShowSettingsFor(ex.Message);
        }
        finally
        {
            progress?.Dispose();
            _loadingAlbums = false;
        }
        QueuePageCheck();
    }

    private async Task LoadAlbumTracksAsync()
    {
        if (_albumTrackPane.IsLoading || _library is not { } library
            || _selectedAlbum is not { } album || _albumTrackPane.NextOffset is not int offset)
            return;
        var token = (_albumTracksRequest ?? _session).Token;
        _albumTrackPane.BeginLoad(offset, "正在读取专辑曲目…", token);
        try
        {
            var page = await library.ReadAlbumTracksAsync(album.Id, offset, album.Name, album.ImageUrl, token);
            if (token.IsCancellationRequested || _selectedAlbum?.Id != album.Id) return;
            _albumTrackPane.AppendPage(page.Items, page.NextOffset, offset, "这张专辑暂无可用曲目");
            _ = PrefetchFavoritesAsync(page.Items, token);
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _albumTrackPane.SetError(ex.Message, canRetry: !ShowSettingsFor(ex.Message));
        }
        finally
        {
            _albumTrackPane.EndLoad();
        }
        QueuePageCheck();
    }

    // ── Search Tab Logic ───────────────────────────────────────────────────────
    private string _searchDetailTitle = string.Empty;
    private string? _searchDetailPlaylistId;
    private string? _searchDetailAlbumId;
    private string? _searchDetailAlbumName;
    private string? _searchDetailAlbumImage;

    private void TriggerSearchNow(bool force = false)
    {
        var query = _searchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            ResetSearchResults();
            return;
        }
        if (!force && string.Equals(query, _lastSearchQuery, StringComparison.Ordinal))
            return;
        _ = RunSearchAsync(query);
    }

    private void ResetSearchResults()
    {
        _lastSearchQuery = string.Empty;
        _searchRequest?.Cancel();
        _searchCollectionsGrid.Children.Clear();
        _searchCollectionsSection.IsVisible = false;
        _searchTracksSection.IsVisible = false;
        _searchTracksPane.ResetTracks();
        _searchMessage.Text = "";
        _searchMessage.IsVisible = false;
        _searchEmptyState.IsVisible = true;
        if (_inSearchDetail)
            ExitSearchDetail();
    }

    private async Task RunSearchAsync(string query)
    {
        if (_library is not { } library)
            return;
        if (_inSearchDetail)
            ExitSearchDetail();
        _lastSearchQuery = query;
        _searchRequest?.Cancel();
        _searchRequest?.Dispose();
        _searchRequest = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        var token = _searchRequest.Token;

        _searchEmptyState.IsVisible = false;
        _searchMessage.Text = $"正在搜索“{query}”…";
        _searchMessage.IsVisible = true;
        _searchCollectionsSection.IsVisible = false;
        _searchTracksSection.IsVisible = false;

        try
        {
            var result = await library.SearchAsync(query, token);
            if (token.IsCancellationRequested) return;

            _searchCollectionsGrid.Children.Clear();
            var cards = new List<Control>();
            foreach (var album in result.Albums.Take(6))
            {
                var card = SearchCollectionCard(
                    album.Name,
                    album.Artists,
                    "专辑",
                    album.ImageUrl,
                    LibraryIcons.Album,
                    () => OpenSearchAlbum(album));
                _searchCollectionsGrid.Children.Add(card);
                cards.Add(card);
            }
            foreach (var playlist in result.Playlists.Take(6))
            {
                var card = SearchCollectionCard(
                    playlist.Name,
                    string.IsNullOrWhiteSpace(playlist.Owner) ? "Spotify" : playlist.Owner,
                    "歌单",
                    playlist.ImageUrl,
                    LibraryIcons.Playlist,
                    () => OpenSearchPlaylist(playlist));
                _searchCollectionsGrid.Children.Add(card);
                cards.Add(card);
            }

            _searchCollectionsSection.IsVisible = cards.Count > 0;
            _searchTracksPane.ResetTracks();
            _searchTracksPane.AppendPage(result.Tracks, null, 0, "");
            _searchTracksSection.IsVisible = result.Tracks.Count > 0;

            if (cards.Count == 0 && result.Tracks.Count == 0)
            {
                _searchMessage.Text = $"未找到与“{query}”相关的内容";
                _searchMessage.IsVisible = true;
            }
            else
            {
                _searchMessage.IsVisible = false;
                if (cards.Count > 0)
                    Motion.Stagger(cards, stepMs: 28, fromY: 10);
            }
            _ = PrefetchFavoritesAsync(result.Tracks, token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (!token.IsCancellationRequested)
            {
                _searchMessage.Text = ex.Message;
                _searchMessage.IsVisible = !ShowSettingsFor(ex.Message);
            }
        }
    }

    private Control SearchCollectionCard(
        string title, string leftMeta, string rightTag, string? imageUrl, string fallbackIcon, Action open)
    {
        var cover = new LibraryArtTile(_artwork, 48, fallbackIcon, LyricsTheme.Accent, imageUrl, cornerRadius: 10);
        var titleBlock = Ui.Text(title, 13, FontWeight.SemiBold);
        titleBlock.TextWrapping = TextWrapping.NoWrap;
        titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        var leftBlock = Ui.Text(leftMeta, 11, muted: true);
        leftBlock.TextWrapping = TextWrapping.NoWrap;
        leftBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        var rightBlock = Ui.Text(rightTag, 10.5, FontWeight.Medium, muted: true);
        rightBlock.TextWrapping = TextWrapping.NoWrap;
        rightBlock.VerticalAlignment = VerticalAlignment.Center;

        var metaRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,Auto"),
            Children = { leftBlock, rightBlock }
        };
        Grid.SetColumn(rightBlock, 2);

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleBlock, metaRow }
        };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("48,10,*"),
            Children = { cover, info }
        };
        Grid.SetColumn(info, 2);

        var button = new Button
        {
            Padding = new Thickness(10),
            Background = LyricsTheme.CardBackground,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = layout,
            Classes = { "glass" }
        };
        button.Click += (_, _) => open();
        TrimmedTooltip(button, titleBlock, leftBlock);
        return new Border { Margin = new Thickness(0, 0, 10, 10), Child = button };
    }

    private string? _searchDetailSpotifyUrl;

    private void OpenSearchAlbum(LibraryAlbum album)
    {
        _searchDetailPlaylistId = null;
        _searchDetailAlbumId = album.Id;
        _searchDetailAlbumName = album.Name;
        _searchDetailAlbumImage = album.ImageUrl;
        _searchDetailTitle = album.Name;
        _searchDetailSpotifyUrl = album.SpotifyUrl;

        _searchDetailPane.ConfigureTitle(album.Name, album.TrackCount > 0 ? album.TrackCount : null);
        EnterSearchDetail();
        _ = LoadSearchDetailMoreAsync();
    }

    private void OpenSearchPlaylist(LibraryPlaylist playlist)
    {
        _searchDetailAlbumId = null;
        _searchDetailAlbumName = null;
        _searchDetailAlbumImage = null;
        _searchDetailPlaylistId = playlist.Id;
        _searchDetailTitle = playlist.Name;
        _searchDetailSpotifyUrl = playlist.SpotifyUrl;

        _searchDetailPane.ConfigureTitle(playlist.Name, playlist.TrackCount);
        EnterSearchDetail();
        _ = LoadSearchDetailMoreAsync();
    }

    private void OpenAlbumFromTrack(LibraryTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.AlbumId))
            return;
        var album = new LibraryAlbum(
            track.AlbumId,
            track.Album ?? "未知专辑",
            track.Artists,
            0,
            null,
            track.AlbumUrl,
            track.ImageUrl);
        SwitchTab(LibraryTab.Search);
        OpenSearchAlbum(album);
    }

    private void EnterSearchDetail()
    {
        _inSearchDetail = true;
        _searchResultsView.IsVisible = false;
        _searchDetailPane.IsVisible = true;
        _searchDetailPane.ResetTracks();
        _searchDetailPane.SetCompactMode(Bounds.Width > 0 && Bounds.Width < 760, showBack: true);
        Motion.In(_searchDetailPane, 24, 0);
        UpdateMiniBar();
    }

    private void ExitSearchDetail()
    {
        if (!_inSearchDetail) return;
        _inSearchDetail = false;
        _searchRequest?.Cancel();
        _searchDetailPane.IsVisible = false;
        _searchResultsView.IsVisible = true;
        Motion.In(_searchResultsView, -20, 0);
        UpdateMiniBar();
    }

    private async Task LoadSearchDetailMoreAsync()
    {
        if (_searchDetailPane.IsLoading || _library is not { } library || _searchDetailPane.NextOffset is not int offset)
            return;
        _searchRequest?.Cancel();
        _searchRequest?.Dispose();
        _searchRequest = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        var token = _searchRequest.Token;
        _searchDetailPane.BeginLoad(offset, "正在读取曲目…", token);
        try
        {
            var page = _searchDetailAlbumId is { } albumId
                ? await library.ReadAlbumTracksAsync(albumId, offset, _searchDetailAlbumName, _searchDetailAlbumImage, token)
                : await library.ReadTracksAsync(_searchDetailPlaylistId, offset, token);
            if (token.IsCancellationRequested) return;
            _searchDetailPane.AppendPage(page.Items, page.NextOffset, offset, "暂无可播放曲目");
            _ = PrefetchFavoritesAsync(page.Items, token);
        }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException ex)
        {
            if (!token.IsCancellationRequested)
                _searchDetailPane.SetError(ex.Message, canRetry: !ShowSettingsFor(ex.Message));
        }
        finally
        {
            _searchDetailPane.EndLoad();
        }
    }

    // ── Filtering & Rows ───────────────────────────────────────────────────────
    private void ApplyCollectionFilter() => ApplyCollectionFilter(animateFirstPage: false);

    private void ApplyCollectionFilter(bool animateFirstPage)
    {
        var query = _collectionFilter.Text?.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allCollections
            : _allCollections.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Owner?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        var previousCount = _visibleCollections.Count;
        _visibleCollections.Clear();
        foreach (var item in filtered)
            _visibleCollections.Add(item);
        if (_visibleCollections.Contains(_selectedCollection))
            _collectionList.SelectedItem = _selectedCollection;
        if (animateFirstPage && previousCount <= 1 && _visibleCollections.Count > 1)
            RevealPage(_collectionList, 0, _visibleCollections.Count);
    }

    private void ApplyAlbumFilter() => ApplyAlbumFilter(animateFirstPage: false);

    private void ApplyAlbumFilter(bool animateFirstPage)
    {
        var query = _albumFilter.Text?.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allAlbums
            : _allAlbums.Where(album =>
                album.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || album.Artists.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var previousCount = _visibleAlbums.Count;
        _visibleAlbums.Clear();
        foreach (var album in filtered)
            _visibleAlbums.Add(album);
        if (_selectedAlbum is not null && _visibleAlbums.Contains(_selectedAlbum))
            _albumList.SelectedItem = _selectedAlbum;
        if (animateFirstPage && previousCount == 0 && _visibleAlbums.Count > 0)
            RevealPage(_albumList, 0, _visibleAlbums.Count);
    }

    private Control CollectionRow(CollectionItem item)
    {
        var button = SidebarNavButton(item.Name, () => SelectCollection(item));
        var tile = new LibraryArtTile(
            _artwork, 34, item.Id is null ? LibraryIcons.HeartFilled : LibraryIcons.Playlist,
            LyricsTheme.Accent, item.ImageUrl, cornerRadius: 7);

        var name = Ui.Text(item.Name, 13, FontWeight.SemiBold);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var leftText = item.Id is null
            ? "我喜欢的音乐"
            : string.IsNullOrWhiteSpace(item.Owner) ? (item.Collaborative ? "协作歌单" : "歌单") : item.Owner;
        var rightText = item.TrackCount is > 0 ? $"{item.TrackCount} 首" : "";

        var leftMeta = Ui.Text(leftText, 11, muted: true);
        leftMeta.TextWrapping = TextWrapping.NoWrap;
        leftMeta.TextTrimming = TextTrimming.CharacterEllipsis;
        var rightMeta = Ui.Text(rightText, 11, muted: true);
        rightMeta.TextWrapping = TextWrapping.NoWrap;
        rightMeta.HorizontalAlignment = HorizontalAlignment.Right;

        var metaRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,Auto"),
            Children = { leftMeta, rightMeta }
        };
        Grid.SetColumn(rightMeta, 2);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, metaRow }
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("34,9,*"),
            Children = { tile, textStack }
        };
        Grid.SetColumn(textStack, 2);
        button.Content = row;
        button.MinHeight = 44;
        TrimmedTooltip(button, name, leftMeta);
        return button;
    }

    private Control AlbumRow(LibraryAlbum album)
    {
        var button = SidebarNavButton(album.Name, () => SelectAlbum(album));
        var tile = new LibraryArtTile(_artwork, 34, LibraryIcons.Album, LyricsTheme.Accent, album.ImageUrl, cornerRadius: 7);

        var name = Ui.Text(album.Name, 13, FontWeight.SemiBold);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var leftMeta = Ui.Text(album.Artists, 11, muted: true);
        leftMeta.TextWrapping = TextWrapping.NoWrap;
        leftMeta.TextTrimming = TextTrimming.CharacterEllipsis;
        var rightMeta = Ui.Text(album.ReleaseYear ?? "", 11, muted: true);
        rightMeta.TextWrapping = TextWrapping.NoWrap;
        rightMeta.HorizontalAlignment = HorizontalAlignment.Right;

        var metaRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,Auto"),
            Children = { leftMeta, rightMeta }
        };
        Grid.SetColumn(rightMeta, 2);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, metaRow }
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("34,9,*"),
            Children = { tile, textStack }
        };
        Grid.SetColumn(textStack, 2);
        button.Content = row;
        button.MinHeight = 44;
        TrimmedTooltip(button, name, leftMeta);
        return button;
    }

    private static Button SidebarNavButton(string label, Action activate)
    {
        var button = new Button
        {
            Content = label,
            CornerRadius = new CornerRadius(10),
            Classes = { "nav" },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(7, 5),
            Margin = new Thickness(0, 1, 14, 1),
            Foreground = LyricsTheme.PrimaryText
        };
        AutomationProperties.SetName(button, label);
        ListBoxItem? container = null;
        void UpdateSelection()
        {
            var active = container?.IsSelected == true;
            button.Classes.Set("navactive", active);
            button.Classes.Set("nav", !active);
        }
        void SelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ListBoxItem.IsSelectedProperty) UpdateSelection();
        }
        button.AttachedToVisualTree += (_, _) =>
        {
            container = button.FindAncestorOfType<ListBoxItem>();
            if (container is not null) container.PropertyChanged += SelectionChanged;
            UpdateSelection();
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            if (container is not null) container.PropertyChanged -= SelectionChanged;
            container = null;
        };
        button.Click += (_, _) => activate();
        return button;
    }

    // ── Playback, Queue & Favorites ────────────────────────────────────────────
    private string ActiveListTitle() => _activeTab switch
    {
        LibraryTab.Collections => _selectedCollection.Name,
        LibraryTab.Recent => "最近播放",
        LibraryTab.Albums => _selectedAlbum?.Name ?? "我的专辑",
        LibraryTab.Search => _inSearchDetail ? _searchDetailTitle : "搜索结果",
        _ => "音乐库"
    };

    private string? ActiveContextUri(LibraryTrackPane pane)
    {
        if (!pane.IsUnfilteredDefaultOrder)
            return null;
        return _activeTab switch
        {
            LibraryTab.Collections when _selectedCollection.Id is { } pid => $"spotify:playlist:{pid}",
            LibraryTab.Albums when _selectedAlbum is { Id: var aid } => $"spotify:album:{aid}",
            LibraryTab.Search when _inSearchDetail && _searchDetailPlaylistId is { } pid => $"spotify:playlist:{pid}",
            LibraryTab.Search when _inSearchDetail && _searchDetailAlbumId is { } aid => $"spotify:album:{aid}",
            _ => null
        };
    }

    internal static LibraryPlayRequest? BuildPlayRequest(
        string? contextUri,
        IReadOnlyList<LibraryTrack> visibleTracks,
        LibraryTrack? startTrack,
        bool? shuffle)
    {
        var playable = visibleTracks.Where(t => t.CanPlay && !string.IsNullOrEmpty(t.Uri)).ToList();
        if (!string.IsNullOrWhiteSpace(contextUri))
        {
            var chosen = startTrack is { CanPlay: true }
                ? startTrack
                : shuffle == true && playable.Count > 0
                    ? playable[Random.Shared.Next(playable.Count)]
                    : null;
            return new LibraryPlayRequest(
                contextUri,
                chosen?.Uri,
                null,
                shuffle,
                chosen?.Uri ?? playable.FirstOrDefault()?.Uri);
        }

        if (playable.Count == 0)
            return startTrack is { CanPlay: true }
                ? new LibraryPlayRequest(null, startTrack.Uri, [startTrack.Uri], shuffle)
                : null;

        var uris = playable.Select(t => t.Uri).Distinct(StringComparer.Ordinal).ToList();
        if (shuffle == true && startTrack is null)
        {
            for (var i = uris.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (uris[i], uris[j]) = (uris[j], uris[i]);
            }
        }

        var startUri = startTrack is { CanPlay: true } ? startTrack.Uri : uris[0];
        var index = uris.FindIndex(u => u == startUri);
        if (index < 0)
        {
            uris.Insert(0, startUri);
            index = 0;
        }

        const int maxSpotifyUris = 100;
        if (uris.Count > maxSpotifyUris)
        {
            var start = Math.Max(0, Math.Min(index, uris.Count - maxSpotifyUris));
            uris = uris.Skip(start).Take(maxSpotifyUris).ToList();
        }
        return new LibraryPlayRequest(null, startUri, uris, shuffle);
    }

    private async Task HandleTrackRowClickAsync(LibraryTrack track)
    {
        if (!track.CanPlay) return;
        if (IsCurrentTrack(track.Uri))
        {
            await HandlePlayPauseAsync();
            return;
        }
        var pane = ActiveTrackPane();
        await PlayFromListAsync(pane.VisibleTracks, track, shuffle: null, ActiveContextUri(pane));
    }

    private async Task PlayFromListAsync(
        IReadOnlyList<LibraryTrack> visibleTracks,
        LibraryTrack? startTrack,
        bool? shuffle,
        string? contextUri)
    {
        if (_library is not { } library) return;
        var request = BuildPlayRequest(contextUri, visibleTracks, startTrack, shuffle);
        if (request is null)
        {
            _showToast("当前列表中没有可播放的曲目");
            return;
        }

        var token = _session.Token;
        try
        {
            var result = await library.PlayContextAsync(request, token);
            if (token.IsCancellationRequested || library != _getLibrary()) return;
            if (result.ManagedByServer)
            {
                _queue.Stop();
                return;
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                _showToast(result.Error);
                return;
            }
            var fallbackUri = request.FallbackTrackUri ?? request.StartTrackUri;
            var fallbackStart = fallbackUri is not null
                ? visibleTracks.FirstOrDefault(t => t.CanPlay && t.Uri == fallbackUri) ?? startTrack
                : startTrack;
            _queue.Start(ActiveListTitle(), visibleTracks, fallbackStart, shuffle == true);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (!token.IsCancellationRequested)
                _showToast(ex.Message);
        }
    }

    private async Task PlaySingleTrackAsync(LibraryTrack track, bool backgroundQueue = false)
    {
        if (_library is not { } library || !track.CanPlay) return;
        var token = (backgroundQueue ? _lifetime : _session).Token;
        try
        {
            var error = await library.PlayTrackAsync(track.Uri, token);
            if (token.IsCancellationRequested || library != _getLibrary()) return;
            if (!string.IsNullOrEmpty(error))
                _showToast(error);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (!token.IsCancellationRequested)
                _showToast(ex.Message);
        }
    }

    private async void PlayQueue(string sourceTitle, IReadOnlyList<LibraryTrack> tracks, LibraryTrack? startTrack, bool shuffle)
    {
        var pane = ActiveTrackPane();
        await PlayFromListAsync(tracks, startTrack, shuffle, ActiveContextUri(pane));
    }

    private async Task PlayQueuedTrackAsync(LibraryTrack track)
    {
        _queue.MarkAdvancing(true);
        try
        {
            await PlaySingleTrackAsync(track, backgroundQueue: true);
        }
        finally
        {
            _queue.MarkAdvancing(false);
        }
    }

    private async Task HandlePreviousAsync()
    {
        if (_queue.IsActive && _queue.MovePrevious() is { } prev)
        {
            await PlayQueuedTrackAsync(prev);
            return;
        }
        await SendControlAsync(PlaybackAction.Previous);
    }

    private async Task HandleNextAsync()
    {
        if (_queue.IsActive && _queue.MoveNext() is { } next)
        {
            await PlayQueuedTrackAsync(next);
            return;
        }
        await SendControlAsync(PlaybackAction.Next);
    }

    private async Task HandlePlayPauseAsync()
    {
        if (_store.Snapshot.Track is null && _queue.Current is { } current)
        {
            await PlayQueuedTrackAsync(current);
            return;
        }
        await SendControlAsync(PlaybackAction.PlayPause);
    }

    private async Task SendControlAsync(PlaybackAction action)
    {
        try
        {
            var error = await _controlPlayback(new PlaybackCommand(action, 0, _store.Snapshot.Track?.Id), _session.Token);
            if (!string.IsNullOrEmpty(error))
                _showToast(error);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _showToast($"播放控制失败：{ex.Message}");
        }
    }

    private async Task ToggleTrackFavoriteAsync(LibraryTrack track)
    {
        if (_library is not { } library || string.IsNullOrEmpty(track.Uri))
            return;
        var previous = _favorites.TryGetValue(track.Uri, out var isFav) && isFav;
        var target = !previous;
        _favorites[track.Uri] = target;
        RefreshAttachedTrackRows();

        var token = _session.Token;
        try
        {
            await library.SetFavoriteAsync(track.Uri, target, token);
            if (token.IsCancellationRequested) return;
            _showToast(target ? $"已收藏《{track.Name}》" : $"已取消收藏《{track.Name}》");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException ex)
        {
            if (token.IsCancellationRequested) return;
            _favorites[track.Uri] = previous;
            RefreshAttachedTrackRows();
            if (!ShowSettingsFor(ex.Message))
                _showToast(ex.Message);
        }
    }

    private async Task PrefetchFavoritesAsync(IReadOnlyList<LibraryTrack> tracks, CancellationToken token)
    {
        if (_library is not { } library) return;
        var missing = tracks
            .Where(t => t.CanPlay && !string.IsNullOrEmpty(t.Uri) && !_favorites.ContainsKey(t.Uri))
            .Select(t => t.Uri)
            .ToArray();
        if (missing.Length == 0) return;
        try
        {
            var states = await library.CheckFavoritesAsync(missing, token);
            if (token.IsCancellationRequested || states.Count == 0) return;
            foreach (var (uri, saved) in states)
                _favorites[uri] = saved;
            RefreshAttachedTrackRows();
        }
        catch (OperationCanceledException) { }
        catch { /* Best-effort favorite state prefetch */ }
    }

    private bool? IsTrackFavorite(string uri) =>
        _favorites.TryGetValue(uri, out var saved) ? saved : null;

    private void RegisterTrackRow(TrackRowView row)
    {
        if (!_attachedTrackRows.Contains(row))
            _attachedTrackRows.Add(row);
    }

    private void UnregisterTrackRow(TrackRowView row) => _attachedTrackRows.Remove(row);

    private void RefreshAttachedTrackRows()
    {
        foreach (var row in _attachedTrackRows)
            row.RefreshDynamicState();
    }

    // ── Store Updates & Mini-Bar ───────────────────────────────────────────────
    private void OnStoreChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested) return;
        var snapshot = _store.Snapshot;
        if (_queue.ObserveSnapshot(snapshot) is { } nextTrack)
            _ = PlayQueuedTrackAsync(nextTrack);
        if (!IsEffectivelyVisible || _session.IsCancellationRequested) return;
        BindLibrary();
        UpdatePlaybackState(snapshot, allowQueueAdvance: false);
    });

    private void UpdatePlaybackState(PlaybackSnapshot snapshot, bool allowQueueAdvance)
    {
        if (allowQueueAdvance && _queue.ObserveSnapshot(snapshot) is { } nextTrack)
            _ = PlayQueuedTrackAsync(nextTrack);

        var nextId = snapshot.Track?.Id;
        var nextPlaying = snapshot.Track is not null && snapshot.IsPlaying;
        var favoriteChanged = false;
        if (snapshot.Track is { Id: var trackId }
            && SongBanner.SpotifyUrl(trackId, PlaybackSourcePreference.Soloist) is not null
            && snapshot.Controls.IsFavorite is bool fav)
        {
            var uri = "spotify:track:" + trackId;
            if (!_favorites.TryGetValue(uri, out var existing) || existing != fav)
            {
                _favorites[uri] = fav;
                favoriteChanged = true;
            }
        }
        if (nextId != _currentTrackId || nextPlaying != _currentIsPlaying || favoriteChanged)
        {
            _currentTrackId = nextId;
            _currentIsPlaying = nextPlaying;
            RefreshAttachedTrackRows();
        }
        UpdateMiniBar();
    }

    private LibraryTrackPane ActiveTrackPane() => _activeTab switch
    {
        LibraryTab.Collections => _collectionTrackPane,
        LibraryTab.Recent => _recentTrackPane,
        LibraryTab.Albums => _albumTrackPane,
        LibraryTab.Search => _inSearchDetail ? _searchDetailPane : _searchTracksPane,
        _ => _collectionTrackPane
    };

    private string? ActiveSpotifyUrl() => _activeTab switch
    {
        LibraryTab.Collections => _selectedCollection.Url,
        LibraryTab.Albums => _selectedAlbum?.SpotifyUrl,
        LibraryTab.Search => _inSearchDetail ? _searchDetailSpotifyUrl : null,
        _ => null
    };

    private static StackPanel DockButtonLabel(string iconPath, string text, IBrush foreground) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            LibraryIcons.Icon(iconPath, 13, foreground),
            new TextBlock
            {
                Text = text,
                LineHeight = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = foreground
            }
        }
    };

    private void UpdateMiniBar()
    {
        var snapshot = _store.Snapshot;
        var track = snapshot.Track;
        var queueCurrent = _queue.Current;
        var activePane = ActiveTrackPane();
        var hasPlayable = activePane.HasPlayable;
        var spotifyUrl = ActiveSpotifyUrl();
        var shouldShow = _queue.IsActive || track is not null || hasPlayable || spotifyUrl is not null;

        if (shouldShow != _miniBarVisible)
        {
            _miniBarVisible = shouldShow;
            _miniBar.IsVisible = shouldShow;
            if (shouldShow)
                SlideDock(0, 14, 0, 0, TimeSpan.FromMilliseconds(260), new CubicEaseOut());
        }
        if (!shouldShow) return;

        if (track is not null)
        {
            _miniTitle.Text = track.Title;
            _miniSubtitle.Text = string.Join(", ", track.Artists);
            _miniCover.SetCover(track.AlbumArtUrl, LibraryIcons.Note);
        }
        else if (queueCurrent is not null)
        {
            _miniTitle.Text = queueCurrent.Name;
            _miniSubtitle.Text = queueCurrent.Artists;
            _miniCover.SetCover(queueCurrent.ImageUrl, LibraryIcons.Note);
        }
        else
        {
            _miniTitle.Text = ActiveListTitle();
            _miniSubtitle.Text = hasPlayable ? "选择曲目或从右侧播放" : "未在播放";
            _miniCover.SetCover(null, LibraryIcons.Playlist);
        }

        _miniPlayIcon.Data = StreamGeometry.Parse(snapshot.IsPlaying ? LibraryIcons.Pause : LibraryIcons.Play);
        _miniPlayIcon.Margin = new Thickness(snapshot.IsPlaying ? 0 : 1.5, 0, 0, 0);
        _miniPlayPause.IsEnabled = _queue.IsActive || (snapshot.Track is not null && snapshot.Controls.CanPlayPause);
        _miniShuffle.IsVisible = _queue.IsActive;
        _miniStopQueue.IsVisible = _queue.IsActive;
        if (_queue.IsActive)
        {
            _miniShuffle.Opacity = _queue.IsShuffled ? 1 : 0.6;
            _miniPrev.IsEnabled = _queue.CanPrevious;
            _miniNext.IsEnabled = _queue.CanNext;
        }
        else
        {
            _miniPrev.IsEnabled = snapshot.Controls.CanPrevious;
            _miniNext.IsEnabled = snapshot.Controls.CanNext;
        }

        _dockPlayAll.IsEnabled = hasPlayable;
        _dockShuffle.IsEnabled = hasPlayable;
        _dockOpenSpotify.IsVisible = spotifyUrl is not null;
        var compactDock = Bounds.Width > 0 && Bounds.Width < 740;
        _dockShuffle.IsVisible = !compactDock;
        _dockOpenSpotify.IsVisible = spotifyUrl is not null && !compactDock;
        SyncDockPalette(animate: true);
    }

    private void OnDockPaletteChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SolidColorBrush.ColorProperty)
            SyncDockPalette(animate: true);
    }

    private void SyncDockPalette(bool animate)
    {
        var nextColor = LyricsTheme.DockBackground.Color;
        if (_dockColor == nextColor)
            return;
        _dockColor = nextColor;

        if (!animate)
        {
            _dockSurface.Transitions = null;
            _dockSurface.Background = new SolidColorBrush(nextColor);
            Dispatcher.UIThread.Post(EnsureDockBackgroundTransition, DispatcherPriority.Render);
            return;
        }

        EnsureDockBackgroundTransition();
        _dockSurface.Background = new SolidColorBrush(nextColor);
    }

    private void EnsureDockBackgroundTransition()
    {
        _dockSurface.Transitions ??=
        [
            new BrushTransition
            {
                Property = Border.BackgroundProperty,
                Duration = TimeSpan.FromMilliseconds(850),
                Easing = new CubicEaseInOut()
            }
        ];
    }

    private bool IsCurrentTrack(string uri) =>
        LibraryTrackQuery.MatchesTrack(_currentTrackId, uri);

    // ── Clipboard & External Links ─────────────────────────────────────────────
    private async Task OpenSpotifyAsync(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host == "open.spotify.com" && TopLevel.GetTopLevel(this) is { } top)
        {
            if (!await top.Launcher.LaunchUriAsync(uri))
                _showToast("无法打开 Spotify 链接");
        }
    }

    private async Task CopyTrackLinkAsync(LibraryTrack track)
    {
        if (string.IsNullOrEmpty(track.SpotifyUrl)) return;
        await CopyTextAsync(track.SpotifyUrl, $"已复制《{track.Name}》链接");
    }

    private async Task CopyTrackInfoAsync(LibraryTrack track)
    {
        var text = string.IsNullOrWhiteSpace(track.Artists) ? track.Name : $"{track.Name} - {track.Artists}";
        await CopyTextAsync(text, "已复制歌曲信息");
    }

    private async Task CopyTextAsync(string text, string successToast)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            _showToast("剪贴板不可用");
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            _showToast(successToast);
        }
        catch (Exception)
        {
            _showToast("复制到剪贴板失败");
        }
    }

    // ── Pagination & Utilities ─────────────────────────────────────────────────
    private void QueuePageCheck()
    {
        if (_pageCheckQueued) return;
        _pageCheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pageCheckQueued = false;
            if (_session.IsCancellationRequested) return;
            if (_activeTab == LibraryTab.Collections)
            {
                if (_collectionOffset.HasValue && !_collectionsFailed && NearEnd(_collectionList))
                    _ = LoadCollectionsAsync();
                if (_collectionTrackPane.ShouldLoadMore)
                    _ = LoadCollectionTracksAsync();
            }
            else if (_activeTab == LibraryTab.Albums)
            {
                if (_albumOffset.HasValue && !_albumsFailed && NearEnd(_albumList))
                    _ = LoadAlbumsAsync();
                if (_albumTrackPane.ShouldLoadMore)
                    _ = LoadAlbumTracksAsync();
            }
            else if (_activeTab == LibraryTab.Search && _inSearchDetail && _searchDetailPane.ShouldLoadMore)
            {
                _ = LoadSearchDetailMoreAsync();
            }
        }, DispatcherPriority.Background);
    }

    internal static bool NearEnd(ListBox list)
    {
        if (!list.IsEffectivelyVisible || list.Scroll is not { } scroll || scroll.Viewport.Height <= 0)
            return false;
        return scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height <= scroll.Viewport.Height * .28;
    }

    internal static void RevealPage(ListBox list, int first, int count)
    {
        if (count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!list.IsEffectivelyVisible) return;
            var added = list.GetRealizedContainers().Where(row =>
            {
                var index = list.IndexFromContainer(row);
                return index >= first && index < first + count;
            }).ToList();
            Motion.Stagger(added, stepMs: 14, fromY: 6);
        }, DispatcherPriority.Loaded);
    }

    private static IDisposable? ShowLoading(TextBlock message, string text, int offset, CancellationToken token)
    {
        message.Text = offset == 0 ? text : "";
        if (offset == 0) return null;
        return DispatcherTimer.RunOnce(() =>
        {
            if (!token.IsCancellationRequested) message.Text = text;
        }, TimeSpan.FromMilliseconds(220));
    }

    private static TextBox CreateSearchBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        MinHeight = 36,
        Padding = new Thickness(12, 7),
        Background = LyricsTheme.InsetBackground,
        BorderBrush = LyricsTheme.CardBorder,
        CornerRadius = new CornerRadius(LyricsTheme.ControlRadius)
    };

    private static Button ClearInputButton()
    {
        var btn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = LibraryIcons.Icon(LibraryIcons.Close, 13, LyricsTheme.MutedText),
            IsVisible = false,
            Classes = { "chrome" }
        };
        AutomationProperties.SetName(btn, "清除搜索");
        return btn;
    }

    private static void BindFilterBox(TextBox box, Button clear, Action onChanged)
    {
        box.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(box.Text);
            onChanged();
        };
        clear.Click += (_, _) =>
        {
            box.Text = string.Empty;
            box.Focus();
        };
    }

    private static ListBox CreateStyledListBox()
    {
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        list.ItemContainerTheme = new ControlTheme(typeof(ListBoxItem))
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((item, _) =>
                    new ContentPresenter
                    {
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        [!ContentPresenter.ContentProperty] = item[!ContentControl.ContentProperty],
                        [!ContentPresenter.ContentTemplateProperty] = item[!ContentControl.ContentTemplateProperty]
                    }))
            }
        };
        return list;
    }

    private static Button IconAction(string label, string path)
    {
        var button = Ui.Button(label);
        AutomationProperties.SetName(button, label);
        button.Width = button.Height = 36;
        button.Padding = new Thickness(0);
        button.Content = LibraryIcons.Icon(path, 16, LyricsTheme.PrimaryText);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(button, label);
        return button;
    }

    private static Button CompactIconButton(string label, string path, double iconSize)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = LibraryIcons.Icon(path, iconSize, LyricsTheme.PrimaryText),
            Classes = { "chrome" }
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    private static void TrimmedTooltip(Control target, params TextBlock[] fields)
    {
        target.PointerEntered += (_, _) =>
        {
            var tip = fields.Any(text => text.TextLayout.TextLines.Any(line => line.HasCollapsed))
                ? string.Join(" — ", fields.Select(text => text.Text).Where(s => !string.IsNullOrWhiteSpace(s)))
                : null;
            ToolTip.SetTip(target, tip);
        };
    }

    private static ContextMenu CreateThemedContextMenu()
    {
        var menu = new ContextMenu
        {
            Padding = new Thickness(5),
            Background = LyricsTheme.DropDownBackground,
            Foreground = LyricsTheme.PrimaryText,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            FontFamily = new FontFamily("Noto Sans CJK SC"),
            FontSize = 13,
            WindowManagerAddShadowHint = true
        };
        menu.Resources["MenuFlyoutPresenterBackground"] = LyricsTheme.DropDownBackground;
        menu.Resources["MenuFlyoutPresenterBorderBrush"] = LyricsTheme.CardBorder;
        menu.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
        menu.Resources["MenuFlyoutPresenterThemePadding"] = new Thickness(5);
        menu.Resources["MenuFlyoutThemeMinHeight"] = 32d;
        menu.Resources["MenuFlyoutItemThemePadding"] = new Thickness(10, 5);
        menu.Resources["MenuFlyoutItemForeground"] = LyricsTheme.PrimaryText;
        menu.Resources["MenuFlyoutItemForegroundPointerOver"] = LyricsTheme.PrimaryText;
        menu.Resources["MenuFlyoutItemForegroundPressed"] = LyricsTheme.PrimaryText;
        menu.Resources["MenuFlyoutItemForegroundDisabled"] = LyricsTheme.MutedText;
        return menu;
    }

    private static MenuItem CreateThemedMenuItem(Control header, Action onClick)
    {
        var highlight = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255))
        {
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Brush.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(140),
                    Easing = new CubicEaseOut()
                }
            }
        };
        var item = new MenuItem
        {
            Header = header,
            MinHeight = 32,
            Padding = new Thickness(10, 5),
            Background = highlight,
            CornerRadius = new CornerRadius(8)
        };
        item.Resources["MenuFlyoutItemBackground"] = highlight;
        item.Resources["MenuFlyoutItemBackgroundPointerOver"] = highlight;
        item.Resources["MenuFlyoutItemBackgroundPressed"] = highlight;
        item.PointerEntered += (_, _) => highlight.Opacity = item.IsEnabled ? 1 : 0;
        item.PointerExited += (_, _) => highlight.Opacity = 0;
        item.Click += (_, _) => onClick();
        return item;
    }

    // ── Nested Reusable Track Pane & Track Row ─────────────────────────────────
    private sealed class LibraryTrackPane : Grid
    {
        private sealed record SortChoice(LibrarySortMode Mode, string Label)
        {
            public override string ToString() => Label;
        }

        private readonly LibraryArtwork _artwork;
        private readonly Func<Task>? _onLoadMore;
        private readonly Action _onTracksChanged;
        private readonly Func<LibraryTrack, Task> _onPlayTrack;
        private readonly Action<LibraryTrack, IReadOnlyList<LibraryTrack>> _onStartQueueFromTrack;
        private readonly Func<LibraryTrack, Task> _onToggleFavorite;
        private readonly Action<LibraryTrack> _onOpenAlbum;
        private readonly Func<string?, Task> _onOpenSpotify;
        private readonly Func<LibraryTrack, Task> _onCopyLink;
        private readonly Func<LibraryTrack, Task> _onCopyInfo;
        private readonly Func<string, bool> _isCurrent;
        private readonly Func<bool> _isPlaying;
        private readonly Func<string, bool?> _isFavorite;
        private readonly Action<TrackRowView> _registerRow;
        private readonly Action<TrackRowView> _unregisterRow;

        private readonly List<LibraryTrack> _rawTracks = [];
        private readonly ObservableCollection<LibraryTrack> _visibleTracks = [];
        private readonly Dictionary<LibraryTrack, int> _originalIndex = new(ReferenceEqualityComparer.Instance);

        private readonly Button _backButton;
        private readonly TextBlock _title = Ui.Text("", 14, FontWeight.SemiBold);
        private readonly TextBox _filterBox = CreateSearchBox("筛选歌曲、歌手或专辑…");
        private readonly Button _filterClear = ClearInputButton();
        private readonly ComboBox _sortCombo = Ui.Combo("排序方式", 110);
        private readonly TextBlock _countLabel = Ui.Text("", 11.5, muted: true);
        private readonly ListBox _list = CreateStyledListBox();
        private readonly TextBlock _message = Ui.Text("", 12, muted: true);
        private readonly Button _retry = Ui.Button("重试");
        private IDisposable? _loadingDelay;
        private int? _totalCount;
        private LibrarySortMode _sortMode = LibrarySortMode.Default;

        public int? NextOffset { get; private set; }
        public bool IsLoading { get; private set; }
        public bool IsFailed { get; private set; }
        public int TotalLoaded => _rawTracks.Count;
        public bool HasPlayable => _visibleTracks.Any(t => t.CanPlay);
        public IReadOnlyList<LibraryTrack> VisibleTracks => _visibleTracks.ToList();
        public bool IsBackVisible => _backButton.IsVisible;
        public bool ShouldLoadMore => NextOffset.HasValue && !IsFailed && !IsLoading && NearEnd(_list);
        public bool IsUnfilteredDefaultOrder => string.IsNullOrWhiteSpace(_filterBox.Text) && _sortMode == LibrarySortMode.Default;

        public LibraryTrackPane(
            LibraryArtwork artwork,
            string? backLabel,
            Action? onBack,
            Func<Task>? onLoadMore,
            Action onTracksChanged,
            Func<LibraryTrack, Task> onPlayTrack,
            Action<LibraryTrack, IReadOnlyList<LibraryTrack>> onStartQueueFromTrack,
            Func<LibraryTrack, Task> onToggleFavorite,
            Action<LibraryTrack> onOpenAlbum,
            Func<string?, Task> onOpenSpotify,
            Func<LibraryTrack, Task> onCopyLink,
            Func<LibraryTrack, Task> onCopyInfo,
            Func<string, bool> isCurrent,
            Func<bool> isPlaying,
            Func<string, bool?> isFavorite,
            Action<TrackRowView> registerRow,
            Action<TrackRowView> unregisterRow,
            bool compactHeader)
        {
            _artwork = artwork;
            _onLoadMore = onLoadMore;
            _onTracksChanged = onTracksChanged;
            _onPlayTrack = onPlayTrack;
            _onStartQueueFromTrack = onStartQueueFromTrack;
            _onToggleFavorite = onToggleFavorite;
            _onOpenAlbum = onOpenAlbum;
            _onOpenSpotify = onOpenSpotify;
            _onCopyLink = onCopyLink;
            _onCopyInfo = onCopyInfo;
            _isCurrent = isCurrent;
            _isPlaying = isPlaying;
            _isFavorite = isFavorite;
            _registerRow = registerRow;
            _unregisterRow = unregisterRow;

            _backButton = new Button
            {
                Height = 30,
                Padding = new Thickness(10, 0),
                Background = LyricsTheme.InputBackground,
                BorderBrush = LyricsTheme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(LyricsTheme.PillRadius),
                Foreground = LyricsTheme.PrimaryText,
                Classes = { "glass" },
                IsVisible = backLabel is not null,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        LibraryIcons.Icon(LibraryIcons.Back, 13, LyricsTheme.PrimaryText),
                        Ui.Text(backLabel ?? "返回", 11.5, FontWeight.Medium)
                    }
                }
            };
            if (onBack is not null)
                _backButton.Click += (_, _) => onBack();

            _title.TextWrapping = TextWrapping.NoWrap;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.MaxWidth = 200;
            _title.IsVisible = false;
            TrimmedTooltip(_title, _title);

            // Toolbar inside track card
            _filterBox.MinHeight = 30;
            _filterBox.Padding = new Thickness(30, 4, 26, 4);
            _filterBox.FontSize = 12.5;
            var filterGlyph = LibraryIcons.Icon(LibraryIcons.Search, 13.5, LyricsTheme.MutedText);
            filterGlyph.HorizontalAlignment = HorizontalAlignment.Left;
            filterGlyph.Margin = new Thickness(10, 0, 0, 0);
            filterGlyph.IsHitTestVisible = false;
            _filterClear.HorizontalAlignment = HorizontalAlignment.Right;
            _filterClear.Margin = new Thickness(0, 0, 4, 0);
            var filterWrap = new Grid { Children = { _filterBox, filterGlyph, _filterClear } };
            BindFilterBox(_filterBox, _filterClear, RebuildVisibleTracks);

            _sortCombo.MinHeight = 30;
            _sortCombo.MinWidth = 104;
            _sortCombo.FontSize = 12;
            var sortItems = new[]
            {
                new SortChoice(LibrarySortMode.Default, "默认顺序"),
                new SortChoice(LibrarySortMode.Title, "按歌名"),
                new SortChoice(LibrarySortMode.Artist, "按歌手"),
                new SortChoice(LibrarySortMode.Duration, "按时长")
            };
            _sortCombo.ItemsSource = sortItems;
            _sortCombo.SelectedIndex = 0;
            _sortCombo.SelectionChanged += (_, _) =>
            {
                if (_sortCombo.SelectedItem is SortChoice choice && _sortMode != choice.Mode)
                {
                    _sortMode = choice.Mode;
                    RebuildVisibleTracks();
                }
            };

            _countLabel.VerticalAlignment = VerticalAlignment.Center;
            _countLabel.HorizontalAlignment = HorizontalAlignment.Right;
            var toolbar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,8,Auto,8,Auto"),
                Children = { _backButton, _title, filterWrap, _sortCombo, _countLabel }
            };
            _backButton.Margin = new Thickness(0, 0, 8, 0);
            _title.Margin = new Thickness(0, 0, 10, 0);
            SetColumn(_title, 1);
            SetColumn(filterWrap, 2);
            SetColumn(_sortCombo, 4);
            SetColumn(_countLabel, 6);

            // List setup
            _list.ItemsSource = _visibleTracks;
            if (compactHeader)
            {
                ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            }
            else
            {
                _list.Margin = new Thickness(0, 0, -8, 0);
                _list.Padding = new Thickness(0, 0, 0, 72);
            }
            _list.ItemTemplate = new FuncDataTemplate<LibraryTrack>((track, _) =>
            {
                if (track is null) return null;
                var idx = _originalIndex.TryGetValue(track, out var n) ? n : 0;
                return BuildTrackRow(track, idx);
            });
            _list.ContainerPrepared += (_, e) =>
            {
                if (e.Index < 0 || e.Index >= _visibleTracks.Count) return;
                e.Container.ContextMenu = BuildContextMenu(_visibleTracks[e.Index]);
            };
            _list.ContainerClearing += (_, e) => e.Container.ContextMenu = null;
            _list.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))) return;
                var origin = e.Source as Control;
                var row = origin as ListBoxItem ?? origin?.FindAncestorOfType<ListBoxItem>();
                if (row?.ContextMenu is not { } menu) return;
                if (!menu.IsOpen) menu.Open(row);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _list.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter && e.Source is ListBoxItem && _list.SelectedItem is LibraryTrack { CanPlay: true } track)
                {
                    e.Handled = true;
                    await _onPlayTrack(track);
                }
            };
            _list.LayoutUpdated += (_, _) =>
            {
                if (ShouldLoadMore && _onLoadMore is not null)
                    _ = _onLoadMore();
            };
            _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
            {
                if (ShouldLoadMore && _onLoadMore is not null)
                    _ = _onLoadMore();
            });

            _message.IsVisible = false;
            _message.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty)
                    _message.IsVisible = !string.IsNullOrEmpty(_message.Text);
            };
            _retry.IsVisible = false;
            _retry.HorizontalAlignment = HorizontalAlignment.Left;
            if (_onLoadMore is not null)
                _retry.Click += async (_, _) => await _onLoadMore();

            var divider = new Border
            {
                Height = 1,
                Background = LyricsTheme.Divider,
                Margin = new Thickness(0, 2, 0, 2)
            };

            var cardGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,6,Auto,4,*,Auto,Auto"),
                Children = { toolbar, divider, _list, _message, _retry }
            };
            SetRow(divider, 2);
            SetRow(_list, 4);
            SetRow(_message, 5);
            SetRow(_retry, 6);

            var unifiedCard = Ui.Card(cardGrid, padding: 12);
            unifiedCard.CornerRadius = new CornerRadius(LyricsTheme.CardRadius);
            RowDefinitions = new RowDefinitions("*");
            Children.Add(unifiedCard);
        }

        public void ConfigureTitle(string title, int? totalCount)
        {
            _title.Text = title;
            _totalCount = totalCount;
            UpdateCountLabel();
        }

        public void SetCompactMode(bool compact, bool showBack)
        {
            _backButton.IsVisible = showBack;
            _title.IsVisible = showBack;
        }

        public void ResetTracks()
        {
            _loadingDelay?.Dispose();
            _loadingDelay = null;
            _rawTracks.Clear();
            _originalIndex.Clear();
            _visibleTracks.Clear();
            _filterBox.Text = string.Empty;
            _sortMode = LibrarySortMode.Default;
            _sortCombo.SelectedIndex = 0;
            _message.Text = "";
            IsLoading = false;
            IsFailed = false;
            NextOffset = 0;
            _retry.IsVisible = false;
            if (_list.Scroll is { } scroll)
                scroll.Offset = default;
            UpdateCountLabel();
            _onTracksChanged();
        }

        public void BeginLoad(int offset, string text, CancellationToken token)
        {
            IsLoading = true;
            IsFailed = false;
            _retry.IsVisible = false;
            _loadingDelay?.Dispose();
            _message.Text = offset == 0 ? text : "";
            _loadingDelay = offset == 0
                ? null
                : DispatcherTimer.RunOnce(() =>
                {
                    if (!token.IsCancellationRequested)
                        _message.Text = text;
                }, TimeSpan.FromMilliseconds(220));
        }

        public void AppendPage(IReadOnlyList<LibraryTrack> items, int? nextOffset, int currentOffset, string emptyMessage)
        {
            var firstVisible = _visibleTracks.Count;
            foreach (var item in items)
            {
                _rawTracks.Add(item);
                _originalIndex[item] = _rawTracks.Count;
            }
            RebuildVisibleTracks(animateAddedFrom: firstVisible);
            NextOffset = nextOffset > currentOffset ? nextOffset : null;
            _message.Text = _rawTracks.Count == 0 && NextOffset is null ? emptyMessage : "";
            _onTracksChanged();
        }

        public void SetError(string message, bool canRetry)
        {
            _message.Text = message;
            IsFailed = true;
            _retry.IsVisible = canRetry;
        }

        public void EndLoad()
        {
            _loadingDelay?.Dispose();
            _loadingDelay = null;
            IsLoading = false;
        }

        private void RebuildVisibleTracks() => RebuildVisibleTracks(animateAddedFrom: null);

        private void RebuildVisibleTracks(int? animateAddedFrom)
        {
            var filtered = LibraryTrackQuery.Apply(_rawTracks, _filterBox.Text, _sortMode);
            var canAppendInPlace = animateAddedFrom.HasValue
                && string.IsNullOrWhiteSpace(_filterBox.Text)
                && _sortMode == LibrarySortMode.Default
                && _visibleTracks.Count <= filtered.Count;

            if (canAppendInPlace)
            {
                var start = _visibleTracks.Count;
                for (var i = start; i < filtered.Count; i++)
                    _visibleTracks.Add(filtered[i]);
                RevealPage(_list, start, filtered.Count - start);
            }
            else
            {
                _visibleTracks.Clear();
                foreach (var item in filtered)
                    _visibleTracks.Add(item);
            }

            if (_rawTracks.Count > 0 && _visibleTracks.Count == 0)
                _message.Text = "没有匹配的歌曲";
            else if (_message.Text == "没有匹配的歌曲")
                _message.Text = "";

            UpdateCountLabel();
            _onTracksChanged();
        }

        private void UpdateCountLabel()
        {
            if (_rawTracks.Count == 0)
            {
                _countLabel.Text = _totalCount is > 0 ? $"共 {_totalCount} 首" : "";
            }
            else if (_visibleTracks.Count == _rawTracks.Count)
            {
                _countLabel.Text = _totalCount is int total && total > _rawTracks.Count
                    ? $"{_rawTracks.Count} / {total} 首"
                    : $"{_rawTracks.Count} 首";
            }
            else
            {
                _countLabel.Text = $"{_visibleTracks.Count} / {_rawTracks.Count} 首";
            }
        }

        private Control BuildTrackRow(LibraryTrack track, int index)
        {
            var row = new TrackRowView(
                _artwork,
                track,
                index,
                () => _isCurrent(track.Uri),
                _isPlaying,
                () => _isFavorite(track.Uri),
                () => _onPlayTrack(track),
                () => _onToggleFavorite(track),
                anchor =>
                {
                    var menu = BuildContextMenu(track);
                    menu.Open(anchor);
                },
                _registerRow,
                _unregisterRow);
            if (ScrollViewer.GetVerticalScrollBarVisibility(_list) != ScrollBarVisibility.Disabled)
                row.Margin = new Thickness(0, 1, 14, 1);
            return row;
        }

        private ContextMenu BuildContextMenu(LibraryTrack track)
        {
            var menu = CreateThemedContextMenu();

            if (track.CanPlay)
            {
                var playingThis = _isCurrent(track.Uri) && _isPlaying();
                menu.Items.Add(CreateMenuItem(
                    playingThis ? "暂停播放" : "立即播放",
                    playingThis ? LibraryIcons.Pause : LibraryIcons.Play,
                    async () => await _onPlayTrack(track),
                    accentIcon: true));

                menu.Items.Add(CreateMenuItem(
                    "从此曲开始连播列表",
                    LibraryIcons.Shuffle,
                    () => _onStartQueueFromTrack(track, _visibleTracks.ToList())));
            }

            if (!string.IsNullOrEmpty(track.Uri))
            {
                var isFav = _isFavorite(track.Uri) == true;
                menu.Items.Add(CreateMenuItem(
                    isFav ? "从「喜欢的歌曲」移除" : "添加到「喜欢的歌曲」",
                    isFav ? LibraryIcons.HeartFilled : LibraryIcons.HeartOutline,
                    async () => await _onToggleFavorite(track),
                    accentIcon: isFav));
            }

            if (!string.IsNullOrEmpty(track.AlbumId))
            {
                menu.Items.Add(CreateMenuItem(
                    $"查看专辑：{track.Album}",
                    LibraryIcons.Album,
                    () => _onOpenAlbum(track)));
            }

            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                menu.Items.Add(new Separator
                {
                    Height = 1,
                    Margin = new Thickness(8, 4),
                    Background = LyricsTheme.Divider
                });
                menu.Items.Add(CreateMenuItem(
                    "复制歌曲链接",
                    LibraryIcons.Copy,
                    async () => await _onCopyLink(track)));
                menu.Items.Add(CreateMenuItem(
                    "复制歌名与歌手",
                    LibraryIcons.Note,
                    async () => await _onCopyInfo(track)));
                menu.Items.Add(CreateMenuItem(
                    "在 Spotify 打开",
                    LibraryIcons.ExternalLink,
                    async () => await _onOpenSpotify(track.SpotifyUrl)));
            }
            return menu;
        }

        private static MenuItem CreateMenuItem(string label, string iconPath, Action onClick, bool accentIcon = false)
        {
            var icon = LibraryIcons.Icon(iconPath, 14, accentIcon ? LyricsTheme.Accent : LyricsTheme.MutedText);
            var text = new TextBlock
            {
                Text = label,
                Foreground = LyricsTheme.PrimaryText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220
            };
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("16,10,*"),
                Children = { icon, text }
            };
            SetColumn(text, 2);
            return CreateThemedMenuItem(content, onClick);
        }

        private static StackPanel ButtonLabel(string iconPath, string text, IBrush foreground) => new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                LibraryIcons.Icon(iconPath, 14, foreground),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground }
            }
        };
    }

    private sealed class TrackRowView : Grid
    {
        private readonly LibraryTrack _track;
        private readonly Func<bool> _isCurrent;
        private readonly Func<bool> _isPlaying;
        private readonly Func<bool?> _isFavorite;
        private readonly LibraryArtTile _art;
        private readonly TextBlock _indexText;
        private readonly LibraryAnimatedLevels _levels = new();
        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;
        private readonly Button _favButton;
        private readonly PathIcon _favIcon;
        private readonly Button _moreButton;
        private readonly Border _hoverBackdrop;
        private bool _hovered;

        public TrackRowView(
            LibraryArtwork artwork,
            LibraryTrack track,
            int index,
            Func<bool> isCurrent,
            Func<bool> isPlaying,
            Func<bool?> isFavorite,
            Func<Task> onPlay,
            Func<Task> onToggleFavorite,
            Action<Control> onOpenMenu,
            Action<TrackRowView> onAttach,
            Action<TrackRowView> onDetach)
        {
            _track = track;
            _isCurrent = isCurrent;
            _isPlaying = isPlaying;
            _isFavorite = isFavorite;

            Margin = new Thickness(0, 1);

            _indexText = Ui.Text(index > 0 ? index.ToString() : "·", 12, muted: true);
            _indexText.HorizontalAlignment = HorizontalAlignment.Center;
            _indexText.VerticalAlignment = VerticalAlignment.Center;
            var indexSlot = new Grid
            {
                Width = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _indexText, _levels }
            };

            _art = new LibraryArtTile(artwork, 34, LibraryIcons.Note, LyricsTheme.Accent, track.ImageUrl, cornerRadius: 7);

            _title = Ui.Text(track.Name, 13.5, FontWeight.SemiBold);
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;

            var leftText = track.Artists;
            var rightText = track.Subtitle ?? track.Album ?? "";
            var leftMeta = Ui.Text(leftText, 11.5, muted: true);
            leftMeta.TextWrapping = TextWrapping.NoWrap;
            leftMeta.TextTrimming = TextTrimming.CharacterEllipsis;
            _subtitle = leftMeta;

            var metadata = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _title, _subtitle }
            };

            var albumMeta = Ui.Text(rightText, 12, muted: true);
            albumMeta.TextWrapping = TextWrapping.NoWrap;
            albumMeta.TextTrimming = TextTrimming.CharacterEllipsis;
            albumMeta.VerticalAlignment = VerticalAlignment.Center;
            albumMeta.HorizontalAlignment = HorizontalAlignment.Left;

            var duration = TimeSpan.FromMilliseconds(track.DurationMs);
            var durationText = Ui.Text(
                track.CanPlay ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}" : "不可播放",
                11.5,
                muted: true);
            durationText.VerticalAlignment = VerticalAlignment.Center;
            durationText.HorizontalAlignment = HorizontalAlignment.Right;
            durationText.MinWidth = 40;
            durationText.TextAlignment = TextAlignment.Right;

            // Reserve right space on the main row button for the sibling Heart + More buttons and scrollbar.
            var mainContent = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("24,8,34,10,1.25*,12,0.85*,12,68,42"),
                Children = { indexSlot, _art, metadata, albumMeta, durationText }
            };
            SetColumn(_art, 2);
            SetColumn(metadata, 4);
            SetColumn(albumMeta, 6);
            SetColumn(durationText, 9);

            var mainButton = new Button
            {
                CornerRadius = new CornerRadius(10),
                Classes = { "seg" },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 5, 14, 5),
                Foreground = LyricsTheme.PrimaryText,
                IsEnabled = track.CanPlay,
                Content = mainContent
            };
            AutomationProperties.SetName(mainButton, $"{track.Name} - {track.Artists}");
            TrimmedTooltip(mainButton, _title, _subtitle, albumMeta);
            mainButton.Click += async (_, _) => await onPlay();

            _hoverBackdrop = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)),
                Opacity = 0,
                IsHitTestVisible = false,
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() }
                }
            };

            _favIcon = LibraryIcons.Icon(LibraryIcons.HeartOutline, 14.5, LyricsTheme.MutedText);
            _favButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = _favIcon,
                IsVisible = !string.IsNullOrEmpty(track.Uri) && track.CanPlay,
                Classes = { "chrome" },
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }
                }
            };
            AutomationProperties.SetName(_favButton, "收藏或取消收藏");
            ToolTip.SetTip(_favButton, "喜欢 / 取消喜欢");
            _favButton.Click += async (_, _) => await onToggleFavorite();

            _moreButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = LibraryIcons.Icon(LibraryIcons.More, 14.5, LyricsTheme.MutedText),
                Classes = { "chrome" },
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }
                }
            };
            AutomationProperties.SetName(_moreButton, "更多操作");
            ToolTip.SetTip(_moreButton, "更多操作");
            _moreButton.Click += (_, _) => onOpenMenu(_moreButton);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 58, 0),
                Children = { _favButton, _moreButton }
            };

            Children.Add(_hoverBackdrop);
            Children.Add(mainButton);
            Children.Add(actions);

            PointerEntered += (_, _) => { _hovered = true; RefreshDynamicState(); };
            PointerExited += (_, _) => { _hovered = false; RefreshDynamicState(); };
            AttachedToVisualTree += (_, _) =>
            {
                onAttach(this);
                RefreshDynamicState();
            };
            DetachedFromVisualTree += (_, _) => onDetach(this);
            RefreshDynamicState();
        }

        public void RefreshDynamicState()
        {
            var current = _isCurrent();
            var playing = current && _isPlaying();
            _title.Foreground = current ? LyricsTheme.Accent : LyricsTheme.PrimaryText;
            _indexText.IsVisible = !current;
            _levels.SetState(current, playing);
            _hoverBackdrop.Opacity = _hovered && _track.CanPlay ? 1 : 0;
            _art.SetPlayOverlay(_hovered && _track.CanPlay, playing);

            var fav = _isFavorite();
            _favIcon.Data = StreamGeometry.Parse(fav == true ? LibraryIcons.HeartFilled : LibraryIcons.HeartOutline);
            _favIcon.Foreground = fav == true ? LyricsTheme.Accent : LyricsTheme.MutedText;
            _favButton.Opacity = fav == true || _hovered ? 1 : 0.42;
            _moreButton.Opacity = _hovered ? 1 : 0.42;
        }
    }
}
