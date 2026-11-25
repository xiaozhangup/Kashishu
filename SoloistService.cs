using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace Kashishu;

internal sealed class SoloistService(
    PlaybackStore store, IslandSettings settings, CancellationToken lifetime) : IPlaybackSource, ISpotifyLibrary
{
    internal const string Starting = "Soloist 正在启动…";
    private const string Pairing = "Soloist 等待配对，请在 Spotify 中选择 Kashishu Soloist";
    private const string Connected = "Soloist 已连接";
    private readonly SpotifyService _library = new(store, settings.SpotifyClientId, settings.SpotifyClientSecret,
        settings.LyricsSource);
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _controls = new(1, 1);
    private SoloistSocket? _socket;
    private string _status = Starting;
    private SourceTrack? _sourceTrack;
    private TrackInfo? _track;
    private CancellationTokenSource? _trackLoad;
    private bool _loadingLyrics;
    private int _lyricsSource = (int)settings.LyricsSource;
    private string _playback = "idle";
    private long _position;
    private long _positionAt;
    private double _speed = 1d;
    private bool _active;
    private bool _shuffle;
    private string _repeat = "off";
    private double? _volume;
    private ImmutableHashSet<string> _availableActions = [];

    public string Status => Volatile.Read(ref _status);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.SoloistApiKey.Length == 0)
                throw new InvalidOperationException("Soloist 请先在设置中填写 API Key");

            foreach (var installed in SoloistExecutable.FindInstalled(settings.SoloistExecutable))
            {
                try
                {
                    await RunClientAsync(installed, cancellationToken);
                    return;
                }
                catch (SoloistBuildExpiredException) { }
            }
            ResetState("Soloist 正在从 Spotify 下载…");
            var executable = await SoloistExecutable.DownloadAsync(cancellationToken);
            ResetState(Starting);
            await RunClientAsync(executable, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (InvalidOperationException exception)
        {
            ResetState(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            ResetState("Soloist 无法访问运行目录或管理进程，请检查权限和正在运行的实例");
        }
    }

    private async Task RunClientAsync(string executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var process = SoloistProcess.Start(executable, settings.SoloistApiKey, settings.SoloistQuality);
        var endpoint = await process.ReadEndpointAsync(cancellationToken);
        // Retry a dropped WebSocket without restarting the paired audio process.
        for (var retry = 0; retry < 3; retry++)
        {
            var connectedAt = Stopwatch.GetTimestamp();
            try
            {
                using var socket = new SoloistSocket();
                await socket.ConnectAsync(endpoint, cancellationToken);
                Volatile.Write(ref _socket, socket);
                await socket.ReceiveAsync(ReadEvent, cancellationToken);
            }
            catch (Exception exception) when (
                exception is WebSocketException or OperationCanceledException or JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                Volatile.Write(ref _socket, null);
                ResetState(Starting);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw process.ExitError();
            if (Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromSeconds(10))
                retry = -1;
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException("Soloist 本地连接已断开，请在设置中重新连接");
    }

    public void SetLyricsSource(LyricsSourcePreference source) =>
        Volatile.Write(ref _lyricsSource, (int)source);

    public bool RequestCurrentTrackRefresh()
    {
        lock (_stateGate)
        {
            if (_sourceTrack is null || _track is null || lifetime.IsCancellationRequested)
                return false;
            StartTrackLoad(_sourceTrack, _track);
            return true;
        }
    }

    public async Task<string?> ControlAsync(PlaybackCommand command, CancellationToken token)
    {
        await _controls.WaitAsync(token);
        try
        {
            var snapshot = store.Snapshot;
            if (command.TrackId is { } id && snapshot.Track?.Id != id)
                return "歌曲已切换，请重试";

            return command.Action switch
            {
                PlaybackAction.PlayPause => await SendAsync(snapshot.IsPlaying ? "pause" : "play", token),
                PlaybackAction.Previous => await SkipPreviousAsync(snapshot, token),
                PlaybackAction.Next => await SendAsync("skip_next", token),
                PlaybackAction.Seek when snapshot.Track is { } track => await SendAsync("seek", token,
                    "position_ms", (long)Math.Clamp(command.Value, 0, track.DurationMs)),
                PlaybackAction.Shuffle => await SendAsync("set_shuffle", token, "enabled", command.Value > 0),
                PlaybackAction.Repeat => await SetRepeatAsync(command.Value, token),
                PlaybackAction.Volume => await SendAsync("set_volume", token, "volume",
                    (int)Math.Round(Math.Clamp(command.Value, 0, 1) * 100)),
                _ => "当前播放状态不支持此操作"
            };
        }
        finally { _controls.Release(); }
    }

    public async Task<string?> PlayTrackAsync(string uri, CancellationToken token)
    {
        if (SongBanner.SpotifyUrl(uri, PlaybackSourcePreference.Soloist) is null
            || !uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            return "这首歌曲无法在 Soloist 播放";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await _controls.WaitAsync(linked.Token);
        try
        {
            if (!Volatile.Read(ref _active) && await SendAsync("activate", linked.Token) is { } error)
                return error;
            return await SendAsync("play", linked.Token, "uri", uri);
        }
        finally { _controls.Release(); }
    }

    public async Task<LibraryPlayResult> PlayContextAsync(LibraryPlayRequest request, CancellationToken token)
    {
        if (request.StartTrackUri is { } startUri
            && (SongBanner.SpotifyUrl(startUri, PlaybackSourcePreference.Soloist) is null
                || !startUri.StartsWith("spotify:track:", StringComparison.Ordinal)))
            return new(false, "这首歌曲无法在 Soloist 播放");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await _controls.WaitAsync(linked.Token);
        try
        {
            var wasActive = Volatile.Read(ref _active);
            if (!wasActive && await SendAsync("activate", linked.Token) is { } activateError)
                return new(false, activateError);

            if (request.Shuffle is bool shuffle)
                await SendAsync("set_shuffle", linked.Token, "enabled", shuffle);

            if (!wasActive)
                await Task.Delay(120, linked.Token);

            if (await _library.StartLibraryPlaybackAsync(request, SoloistProcess.DeviceName, linked.Token))
                return new(true, null);

            if (request.StartTrackUri is { } fallbackUri)
                return new(false, await SendAsync("play", linked.Token, "uri", fallbackUri));

            return new(false, "当前无法从此列表开始播放");
        }
        finally { _controls.Release(); }
    }

    public async Task<LibraryPageResult<LibraryPlaylist>> ReadPlaylistsAsync(int offset, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.ReadPlaylistsAsync(offset, linked.Token);
    }

    public async Task<LibraryPageResult<LibraryTrack>> ReadTracksAsync(
        string? playlistId, int offset, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.ReadTracksAsync(playlistId, offset, linked.Token);
    }

    public async Task<LibraryPageResult<LibraryTrack>> ReadRecentlyPlayedAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.ReadRecentlyPlayedAsync(linked.Token);
    }

    public async Task<LibraryPageResult<LibraryAlbum>> ReadAlbumsAsync(int offset, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.ReadAlbumsAsync(offset, linked.Token);
    }

    public async Task<LibraryPageResult<LibraryTrack>> ReadAlbumTracksAsync(
        string albumId, int offset, string? albumName, string? albumImageUrl, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.ReadAlbumTracksAsync(albumId, offset, albumName, albumImageUrl, linked.Token);
    }

    public async Task<LibrarySearchResult> SearchAsync(string query, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.SearchAsync(query, linked.Token);
    }

    public async Task<IReadOnlyDictionary<string, bool>> CheckFavoritesAsync(
        IReadOnlyList<string> uris, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await _library.CheckFavoritesAsync(uris, linked.Token);
    }

    public async Task SetFavoriteAsync(string uri, bool saved, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await _library.SetLibraryFavoriteAsync(uri, saved, linked.Token);
    }

    internal Task WaitForLibraryAsync() => _library.WaitForLibraryAsync();

    private const long PreviousTrackSeekThresholdMs = 1_500;

    internal static bool ShouldSeekBeforeSkipPrevious(PlaybackSnapshot snapshot) =>
        snapshot.Track is not null && snapshot.Controls.CanSeek && snapshot.PositionMs >= PreviousTrackSeekThresholdMs;

    private async Task<string?> SkipPreviousAsync(PlaybackSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Track is not { Id: var trackId })
            return await SendAsync("skip_prev", token);

        if (ShouldSeekBeforeSkipPrevious(snapshot))
        {
            if (await SendAsync("seek", token, "position_ms", 0L) is null)
                await WaitForStoreConditionAsync(
                    s => s.Track?.Id != trackId || s.PositionMs < PreviousTrackSeekThresholdMs,
                    TimeSpan.FromMilliseconds(140),
                    token);
            return await SendAsync("skip_prev", token);
        }

        var error = await SendAsync("skip_prev", token);
        if (error is not null || snapshot.PositionMs < PreviousTrackSeekThresholdMs)
            return error;

        // If seeking isn't advertised, retry skip_prev once when Spotify only restarted the current track.
        var restarted = await WaitForStoreConditionAsync(
            s => s.Track?.Id != trackId || s.PositionMs < PreviousTrackSeekThresholdMs,
            TimeSpan.FromMilliseconds(180),
            token);
        return restarted && store.Snapshot is { Track.Id: var id, PositionMs: < PreviousTrackSeekThresholdMs } && id == trackId
            ? await SendAsync("skip_prev", token)
            : null;
    }

    private async Task<bool> WaitForStoreConditionAsync(
        Func<PlaybackSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken token)
    {
        if (predicate(store.Snapshot))
            return true;

        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (predicate(store.Snapshot))
                reached.TrySetResult(true);
        }

        store.Changed += OnChanged;
        try
        {
            if (predicate(store.Snapshot))
                return true;
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var delayTask = Task.Delay(timeout, delayCts.Token);
            var completed = await Task.WhenAny(reached.Task, delayTask);
            if (completed == reached.Task)
            {
                delayCts.Cancel();
                return true;
            }
            token.ThrowIfCancellationRequested();
            return predicate(store.Snapshot);
        }
        finally
        {
            store.Changed -= OnChanged;
        }
    }

    private async Task<string?> SetRepeatAsync(double mode, CancellationToken token)
    {
        var first = mode == 2 ? "set_repeat_context" : "set_repeat_track";
        var second = mode == 2 ? "set_repeat_track" : "set_repeat_context";
        return await SendAsync(first, token, "enabled", false)
            ?? await SendAsync(second, token, "enabled", mode > 0);
    }

    private Task<string?> SendAsync(string command, CancellationToken token,
        string? field = null, object? value = null)
    {
        var status = Status;
        var socket = Volatile.Read(ref _socket);
        return status == Connected && socket is not null
            ? socket.SendAsync(command, field, value, token)
            : Task.FromResult<string?>(status);
    }

    private void ReadEvent(JsonElement root)
    {
        if (lifetime.IsCancellationRequested)
            return;
        lock (_stateGate)
        {
            var previous = store.Snapshot;
            var now = Stopwatch.GetTimestamp();
            var type = String(root, "type");
            if (Status != Connected && type != "auth_state")
                return;
            switch (type)
            {
                case "auth_state":
                    if (Boolean(root, "logged_in") != true)
                    {
                        ResetState(Pairing);
                        return;
                    }
                    _active = Boolean(root, "is_active") == true;
                    Volatile.Write(ref _status, Connected);
                    break;
                case "playback_state":
                    _active = Boolean(root, "is_active") == true;
                    _playback = String(root, "status") ?? "idle";
                    _availableActions = Property(root, "available_actions") is { ValueKind: JsonValueKind.Object } actions
                        ? actions.EnumerateObject().Select(action => action.Name).ToImmutableHashSet() : [];
                    SetTrack(Property(root, "item"));
                    SetPosition(Property(root, "position"), now);
                    SetOptions(Property(root, "options"));
                    _volume = Number(root, "volume") is { } initialVolume
                        ? Math.Clamp(initialVolume / 100, 0, 1) : _volume;
                    break;
                case "device_changed":
                    _active = Boolean(root, "is_active") == true;
                    break;
                case "playback_changed":
                    _position = (long)previous.PositionMs;
                    _positionAt = now;
                    _playback = String(root, "status") ?? "idle";
                    if (_playback == "playing" && _speed <= 0) _speed = 1d;
                    break;
                case "track_changed":
                    if (SetTrack(Property(root, "item")))
                    {
                        _position = 0;
                        _positionAt = now;
                    }
                    break;
                case "position_sync":
                    SetPosition(Property(root, "position"), now);
                    break;
                case "volume_changed":
                    if (Number(root, "volume") is { } volume)
                        _volume = Math.Clamp(volume / 100, 0, 1);
                    break;
                case "options_changed":
                    SetOptions(Property(root, "options"));
                    break;
                default:
                    return;
            }
            Publish();
            if (Status == Connected && type is "auth_state" or "device_changed" or "playback_changed" or "track_changed")
                Volatile.Read(ref _socket)?.RequestState(lifetime);
        }
    }

    private void SetPosition(JsonElement position, long now)
    {
        _speed = Math.Max(0, Number(position, "speed") ?? 0);
        var elapsed = Number(position, "timestamp_ms") is { } timestamp
            ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp) : 0;
        _position = (long)Math.Max(0, (Number(position, "position_ms") ?? 0) + elapsed * _speed);
        _positionAt = now;
    }

    private void SetOptions(JsonElement options)
    {
        _shuffle = Boolean(options, "shuffle") ?? _shuffle;
        _repeat = String(options, "repeat") ?? _repeat;
    }

    private void Publish()
    {
        if (lifetime.IsCancellationRequested)
            return;
        var track = _active && _playback != "idle" ? _track : null;
        var status = Status != Connected ? Status
            : track is null ? PlaybackStore.NoActiveSoloistPlaybackStatus
            : track.Lyrics.IsDefaultOrEmpty ? (_loadingLyrics ? "正在获取歌词…" : "暂无歌词")
            : string.Join(", ", track.Artists);
        var controls = track is null ? new PlaybackControls() : new PlaybackControls(
            _availableActions.Contains("play") || _availableActions.Contains("pause"),
            _availableActions.Contains("skip_prev"),
            _availableActions.Contains("skip_next"),
            _availableActions.Contains("seek"),
            _availableActions.Contains("shuffle") ? _shuffle : null,
            _availableActions.Contains("set_repeat") ? _repeat : null,
            _volume);
        store.Update(new PlaybackSnapshot(track, _position, _positionAt, track is not null && _playback == "playing",
            status, _speed) { Controls = controls });
    }

    private void ResetState(string status)
    {
        lock (_stateGate)
        {
            Volatile.Write(ref _status, status);
            _sourceTrack = null;
            _track = null;
            _playback = "idle";
            _active = false;
            _availableActions = [];
            _volume = null;
            _position = 0;
            _positionAt = Stopwatch.GetTimestamp();
            CancelTrackLoad();
            Publish();
        }
    }

    private bool SetTrack(JsonElement item)
    {
        var source = ParseEntity(item);
        if (source is null)
        {
            var changed = _track is not null;
            CancelTrackLoad();
            _sourceTrack = null;
            _track = null;
            return changed;
        }

        var changedId = _sourceTrack?.Id != source.Id;
        if (!changedId && _sourceTrack is { } last && last.Title == source.Title
            && last.DurationMs == source.DurationMs && last.Album == source.Album
            && last.AlbumArtUrl == source.AlbumArtUrl && last.Artists.SequenceEqual(source.Artists))
            return false;

        var cached = !changedId ? _track : TrackCache.Load(source.Id);
        var track = cached ?? new TrackInfo(source.Id, source.Title, source.Artists, source.Album,
            source.DurationMs, [], []);
        track = track with
        {
            Title = source.Title,
            Artists = source.Artists,
            Album = source.Album,
            DurationMs = source.DurationMs,
            AlbumArtUrl = source.AlbumArtUrl,
            ShareUrl = source.ShareUrl
        };
        _sourceTrack = source;
        _track = track;
        StartTrackLoad(source, track);
        return changedId;
    }

    private void StartTrackLoad(SourceTrack source, TrackInfo track)
    {
        CancelTrackLoad();
        _trackLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = _trackLoad.Token;
        var lyricsSource = (LyricsSourcePreference)Volatile.Read(ref _lyricsSource);
        _loadingLyrics = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var lyrics = SpotifyService.LoadLyricsSafeAsync(source, lyricsSource, token);
                var cover = SpotifyService.LoadImageSafeAsync(source.AlbumArtUrl, token);

                async Task PublishLyricsAsync()
                {
                    var loaded = await lyrics;
                    if (!loaded.IsDefaultOrEmpty)
                        track = track with { Lyrics = loaded, LyricsSource = lyricsSource };
                    UpdateTrack(lyricsLoaded: true);
                }

                async Task PublishCoverAsync()
                {
                    var image = TrackCache.PreferLargerImage(track.AlbumArtBytes, await cover);
                    if (!image.Equals(track.AlbumArtBytes))
                    {
                        track = track with { AlbumArtBytes = image };
                        UpdateTrack(lyricsLoaded: false);
                    }
                }

                void UpdateTrack(bool lyricsLoaded)
                {
                    lock (_stateGate)
                    {
                        token.ThrowIfCancellationRequested();
                        _track = track;
                        if (lyricsLoaded)
                            _loadingLyrics = false;
                        Publish();
                    }
                }

                if (await Task.WhenAny(lyrics, cover) == lyrics)
                {
                    await PublishLyricsAsync();
                    await PublishCoverAsync();
                }
                else
                {
                    await PublishCoverAsync();
                    await PublishLyricsAsync();
                }
                token.ThrowIfCancellationRequested();
                TrackCache.SaveIfChanged(track);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }

    private void CancelTrackLoad()
    {
        _trackLoad?.Cancel();
        _trackLoad?.Dispose();
        _trackLoad = null;
        _loadingLyrics = false;
    }

    private static SourceTrack? ParseEntity(JsonElement item)
    {
        const string prefix = "spotify:track:";
        var uri = String(item, "uri");
        if (uri is null || !uri.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var metadata = Property(item, "decorations");
        if (EntityName(item) is not { Length: > 0 } name)
            return null;
        var album = Property(Property(metadata, "parent"), "entity");
        var creators = Property(metadata, "creators");
        var artists = creators.ValueKind == JsonValueKind.Array
            ? creators.EnumerateArray().Select(creator => EntityName(Property(creator, "entity")))
                .OfType<string>().ToImmutableArray()
            : [];
        var covers = Property(Property(metadata, "visual_identity"), "cover");
        var cover = covers.ValueKind == JsonValueKind.Array
            ? covers.EnumerateArray().OrderByDescending(image => String(image, "size") switch
                { "xlarge" => 4, "large" => 3, "default" => 2, _ => 1 })
                .Select(image => String(image, "url")).OfType<string>().FirstOrDefault()
            : null;
        return new SourceTrack(uri[prefix.Length..], name, artists, [], [], EntityName(album) ?? string.Empty,
            (long)Math.Max(0, Number(Property(metadata, "playback"), "duration_ms") ?? 0), null, cover)
        {
            ShareUrl = SongBanner.SpotifyUrl(uri, PlaybackSourcePreference.Soloist)
        };
    }

    internal static string? String(JsonElement parent, string key) =>
        Property(parent, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? Number(JsonElement parent, string key) =>
        Property(parent, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number)
            ? number : null;

    private static bool? Boolean(JsonElement parent, string key) => Property(parent, key).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string? EntityName(JsonElement entity) =>
        String(Property(Property(entity, "decorations"), "identity"), "name");

    private static JsonElement Property(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) ? value : default;
}
