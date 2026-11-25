using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kashishu;

internal interface ISpotifyLibrary
{
    string Status { get; }
    Task<LibraryPageResult<LibraryPlaylist>> ReadPlaylistsAsync(int offset, CancellationToken token);
    Task<LibraryPageResult<LibraryTrack>> ReadTracksAsync(string? playlistId, int offset, CancellationToken token);
    Task<LibraryPageResult<LibraryTrack>> ReadRecentlyPlayedAsync(CancellationToken token);
    Task<LibraryPageResult<LibraryAlbum>> ReadAlbumsAsync(int offset, CancellationToken token);
    Task<LibraryPageResult<LibraryTrack>> ReadAlbumTracksAsync(
        string albumId, int offset, string? albumName, string? albumImageUrl, CancellationToken token);
    Task<LibrarySearchResult> SearchAsync(string query, CancellationToken token);
    Task<IReadOnlyDictionary<string, bool>> CheckFavoritesAsync(IReadOnlyList<string> uris, CancellationToken token);
    Task SetFavoriteAsync(string uri, bool saved, CancellationToken token);
    Task<string?> PlayTrackAsync(string uri, CancellationToken token);
    Task<LibraryPlayResult> PlayContextAsync(LibraryPlayRequest request, CancellationToken token);
}

internal sealed record LibraryPlayRequest(
    string? ContextUri,
    string? StartTrackUri,
    IReadOnlyList<string>? TrackUris = null,
    bool? Shuffle = null);

internal sealed record LibraryPlayResult(bool ManagedByServer, string? Error = null);

internal sealed record LibraryPageResult<T>(IReadOnlyList<T> Items, int? NextOffset, int? Total = null);

internal sealed record LibraryPlaylist(
    string Id,
    string Name,
    string? SpotifyUrl,
    string? ImageUrl = null,
    string? Owner = null,
    int? TrackCount = null,
    string? Description = null,
    bool Collaborative = false);

internal sealed record LibraryTrack(
    string Uri,
    string Name,
    string Artists,
    long DurationMs,
    string? SpotifyUrl,
    bool CanPlay,
    string? ImageUrl = null,
    string? Album = null,
    string? AlbumId = null,
    string? AlbumUrl = null,
    string? Subtitle = null);

internal sealed record LibraryAlbum(
    string Id,
    string Name,
    string Artists,
    int TrackCount,
    string? ReleaseYear,
    string? SpotifyUrl,
    string? ImageUrl = null);

internal sealed record LibrarySearchResult(
    IReadOnlyList<LibraryTrack> Tracks,
    IReadOnlyList<LibraryAlbum> Albums,
    IReadOnlyList<LibraryPlaylist> Playlists);

internal sealed partial class SpotifyService
{
    private const string PlaylistReadScope = "playlist-read-private";
    private const string CollaborativeScope = "playlist-read-collaborative";
    private const string LibraryScope = Scope + " " + PlaylistReadScope + " " + CollaborativeScope;
    private const int LibraryPageSize = 50;

    internal async Task<LibraryPageResult<LibraryPlaylist>> ReadPlaylistsAsync(int offset, CancellationToken token)
    {
        using var result = await ReadLibraryPageAsync("/me/playlists", offset, token);
        var root = result.RootElement;
        var playlists = new List<LibraryPlaylist>();
        string? currentUser = null;
        foreach (var playlist in root.GetProperty("items").EnumerateArray())
        {
            if (playlist.ValueKind != JsonValueKind.Object
                || GetString(playlist, "id") is not { } id
                || GetString(playlist, "name") is not { } name)
                continue;

            var hasItems = playlist.TryGetProperty("items", out var contents) && contents.ValueKind == JsonValueKind.Object;
            var hasTracks = playlist.TryGetProperty("tracks", out var legacy) && legacy.ValueKind == JsonValueKind.Object;
            if (!hasItems)
            {
                // Older responses include a tracks summary, which is not proof of access.
                if (!hasTracks)
                    continue;
                if (currentUser is null)
                {
                    using var profile = await ReadLibraryJsonAsync("/me", false, token);
                    currentUser = GetString(profile.RootElement, "id")
                        ?? throw new InvalidOperationException("Spotify 未返回当前账号的 ID");
                }
                var owned = playlist.TryGetProperty("owner", out var ownerObj) && ownerObj.ValueKind == JsonValueKind.Object
                    && GetString(ownerObj, "id") == currentUser;
                if (!owned && (!GetBoolean(playlist, "collaborative") || !await CanReadPlaylistAsync(id, token)))
                    continue;
            }
            playlists.Add(ParsePlaylistObject(playlist, id, name));
        }
        return new LibraryPageResult<LibraryPlaylist>(
            playlists,
            NextLibraryOffset(root, offset),
            (int?)GetNullableInt64(root, "total"));
    }

    internal async Task<LibraryPageResult<LibraryTrack>> ReadTracksAsync(
        string? playlistId, int offset, CancellationToken token)
    {
        var path = playlistId is null ? "/me/tracks" : $"/playlists/{Uri.EscapeDataString(playlistId)}/items";
        using var result = await ReadLibraryPageAsync(path, offset, token);
        return ParseTracksPage(result.RootElement, offset);
    }

    internal async Task<LibraryPageResult<LibraryTrack>> ReadRecentlyPlayedAsync(CancellationToken token)
    {
        using var result = await ReadLibraryJsonAsync("/me/player/recently-played?limit=50", true, token);
        return ParseRecentlyPlayedPage(result.RootElement, DateTimeOffset.UtcNow);
    }

    internal async Task<LibraryPageResult<LibraryAlbum>> ReadAlbumsAsync(int offset, CancellationToken token)
    {
        using var result = await ReadLibraryPageAsync("/me/albums", offset, token);
        return ParseAlbumsPage(result.RootElement, offset);
    }

    internal async Task<LibraryPageResult<LibraryTrack>> ReadAlbumTracksAsync(
        string albumId, int offset, string? albumName, string? albumImageUrl, CancellationToken token)
    {
        var path = $"/albums/{Uri.EscapeDataString(albumId)}/tracks";
        using var result = await ReadLibraryPageAsync(path, offset, token);
        return ParseAlbumTracksPage(result.RootElement, offset, albumId, albumName, albumImageUrl);
    }

    internal async Task<LibrarySearchResult> SearchAsync(string query, CancellationToken token)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return new LibrarySearchResult([], [], []);
        var path = $"/search?q={Uri.EscapeDataString(trimmed)}&type=track,album,playlist&limit=15";
        using var result = await ReadLibraryJsonAsync(path, paged: false, token);
        return ParseSearchResult(result.RootElement);
    }

    internal async Task<IReadOnlyDictionary<string, bool>> CheckFavoritesAsync(
        IReadOnlyList<string> uris, CancellationToken token)
    {
        var valid = uris
            .Where(uri => uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (valid.Length == 0)
            return map;

        // Batch in chunks of 40 (Spotify /me/library/contains limit).
        for (var i = 0; i < valid.Length; i += 40)
        {
            var chunk = valid.Skip(i).Take(40).ToArray();
            var joined = string.Join(",", chunk);
            using var result = await SendLibraryJsonAsync(HttpMethod.Get,
                "/me/library/contains?uris=" + Uri.EscapeDataString(joined), token);
            if (result is null || result.RootElement.ValueKind != JsonValueKind.Array)
                continue;
            var array = result.RootElement;
            var count = Math.Min(chunk.Length, array.GetArrayLength());
            for (var index = 0; index < count; index++)
            {
                if (array[index].ValueKind is JsonValueKind.True or JsonValueKind.False)
                    map[chunk[index]] = array[index].GetBoolean();
            }
        }
        return map;
    }

    internal async Task SetLibraryFavoriteAsync(string uri, bool saved, CancellationToken token)
    {
        if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            throw new InvalidOperationException("当前曲目不支持 Spotify 收藏");
        using var response = await SendLibraryRequestAsync(
            saved ? HttpMethod.Put : HttpMethod.Delete,
            "/me/library?uris=" + Uri.EscapeDataString(uri),
            token);
    }

    private string? _soloistDeviceId;

    internal static string BuildPlayPayload(LibraryPlayRequest request)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.ContextUri))
            payload["context_uri"] = request.ContextUri;
        else if (request.TrackUris is { Count: > 0 } uris)
            payload["uris"] = uris;
        else if (!string.IsNullOrWhiteSpace(request.StartTrackUri))
            payload["uris"] = new[] { request.StartTrackUri };

        if (!string.IsNullOrWhiteSpace(request.StartTrackUri))
            payload["offset"] = new Dictionary<string, string> { ["uri"] = request.StartTrackUri };

        return JsonSerializer.Serialize(payload);
    }

    internal async Task<bool> StartLibraryPlaybackAsync(
        LibraryPlayRequest request,
        string preferredDeviceName,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ContextUri)
            && (request.TrackUris is null || request.TrackUris.Count == 0)
            && string.IsNullOrWhiteSpace(request.StartTrackUri))
            return false;

        await _playerGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_credentials.ClientId.Length == 0 || _credentials.ClientSecret.Length == 0)
                return false;

            _tokens ??= await LoadTokenAsync(token);
            if (!HasScope(ControlScope))
                return false;
            if (_tokens!.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                _tokens = await RefreshLibraryAsync(token);

            var payload = BuildPlayPayload(request);
            var deviceId = Volatile.Read(ref _soloistDeviceId);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var path = string.IsNullOrEmpty(deviceId)
                    ? "/me/player/play"
                    : "/me/player/play?device_id=" + Uri.EscapeDataString(deviceId);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var httpRequest = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1" + path)
                {
                    Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
                };
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
                using var response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _positionSyncVersion);
                    _pollWake.Release();
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _tokens = await RefreshLibraryAsync(token);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound && attempt < 2)
                {
                    if (attempt > 0)
                        await Task.Delay(200, token);
                    deviceId = await ResolveDeviceIdAsync(preferredDeviceName, token);
                    Volatile.Write(ref _soloistDeviceId, deviceId);
                    if (!string.IsNullOrEmpty(deviceId))
                        continue;
                    if (attempt == 0)
                        continue;
                }
                return false;
            }
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
        finally
        {
            _playerGate.Release();
        }
    }

    private async Task<string?> ResolveDeviceIdAsync(string preferredDeviceName, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var device in devices.EnumerateArray())
        {
            if (device.ValueKind == JsonValueKind.Object
                && GetString(device, "name") == preferredDeviceName
                && GetString(device, "id") is { Length: > 0 } id)
                return id;
        }
        return null;
    }

    internal static LibraryPageResult<LibraryTrack> ParseTracksPage(JsonElement root, int offset)
    {
        var tracks = new List<LibraryTrack>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || (!entry.TryGetProperty("item", out var track) && !entry.TryGetProperty("track", out track))
                    || track.ValueKind != JsonValueKind.Object)
                    continue;
                if (ParseTrackObject(track, GetBoolean(entry, "is_local")) is { } parsed)
                    tracks.Add(parsed);
            }
        }
        return new LibraryPageResult<LibraryTrack>(
            tracks,
            NextLibraryOffset(root, offset),
            (int?)GetNullableInt64(root, "total"));
    }

    internal static LibraryPageResult<LibraryTrack> ParseRecentlyPlayedPage(JsonElement root, DateTimeOffset now)
    {
        var tracks = new List<LibraryTrack>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("track", out var track)
                    || track.ValueKind != JsonValueKind.Object)
                    continue;
                var subtitle = FormatPlayedAt(GetString(entry, "played_at"), now);
                if (ParseTrackObject(track, GetBoolean(entry, "is_local"), subtitle: subtitle) is { } parsed)
                    tracks.Add(parsed);
            }
        }
        return new LibraryPageResult<LibraryTrack>(tracks, null, tracks.Count);
    }

    internal static LibraryPageResult<LibraryAlbum> ParseAlbumsPage(JsonElement root, int offset)
    {
        var albums = new List<LibraryAlbum>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("album", out var album)
                    || album.ValueKind != JsonValueKind.Object)
                    continue;
                if (ParseAlbumObject(album) is { } parsed)
                    albums.Add(parsed);
            }
        }
        return new LibraryPageResult<LibraryAlbum>(
            albums,
            NextLibraryOffset(root, offset),
            (int?)GetNullableInt64(root, "total"));
    }

    internal static LibraryPageResult<LibraryTrack> ParseAlbumTracksPage(
        JsonElement root, int offset, string albumId, string? albumName, string? albumImageUrl)
    {
        var tracks = new List<LibraryTrack>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in items.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object)
                    continue;
                if (ParseTrackObject(track, false, albumId, albumName, albumImageUrl, allowMissingType: true) is { } parsed)
                    tracks.Add(parsed);
            }
        }
        return new LibraryPageResult<LibraryTrack>(
            tracks,
            NextLibraryOffset(root, offset),
            (int?)GetNullableInt64(root, "total"));
    }

    internal static LibrarySearchResult ParseSearchResult(JsonElement root)
    {
        var tracks = new List<LibraryTrack>();
        if (root.TryGetProperty("tracks", out var tracksObj) && tracksObj.ValueKind == JsonValueKind.Object
            && tracksObj.TryGetProperty("items", out var trackItems) && trackItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in trackItems.EnumerateArray())
            {
                if (track.ValueKind == JsonValueKind.Object && ParseTrackObject(track, false) is { } parsed)
                    tracks.Add(parsed);
            }
        }

        var albums = new List<LibraryAlbum>();
        if (root.TryGetProperty("albums", out var albumsObj) && albumsObj.ValueKind == JsonValueKind.Object
            && albumsObj.TryGetProperty("items", out var albumItems) && albumItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var album in albumItems.EnumerateArray())
            {
                if (album.ValueKind == JsonValueKind.Object && ParseAlbumObject(album) is { } parsed)
                    albums.Add(parsed);
            }
        }

        var playlists = new List<LibraryPlaylist>();
        if (root.TryGetProperty("playlists", out var playlistsObj) && playlistsObj.ValueKind == JsonValueKind.Object
            && playlistsObj.TryGetProperty("items", out var playlistItems) && playlistItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var playlist in playlistItems.EnumerateArray())
            {
                if (playlist.ValueKind != JsonValueKind.Object
                    || GetString(playlist, "id") is not { } id
                    || GetString(playlist, "name") is not { } name)
                    continue;
                playlists.Add(ParsePlaylistObject(playlist, id, name));
            }
        }

        return new LibrarySearchResult(tracks, albums, playlists);
    }

    internal static LibraryPlaylist ParsePlaylistObject(JsonElement playlist, string id, string name)
    {
        string? ownerName = null;
        if (playlist.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
            ownerName = GetString(owner, "display_name") ?? GetString(owner, "id");

        int? trackCount = null;
        if (playlist.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            trackCount = (int?)GetNullableInt64(items, "total");
        if (trackCount is null && playlist.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Object)
            trackCount = (int?)GetNullableInt64(tracks, "total");

        var description = CleanPlaylistDescription(GetString(playlist, "description"));
        return new LibraryPlaylist(
            id,
            name,
            $"https://open.spotify.com/playlist/{id}",
            LibraryImage(playlist),
            ownerName,
            trackCount,
            description,
            GetBoolean(playlist, "collaborative"));
    }

    private static LibraryAlbum? ParseAlbumObject(JsonElement album)
    {
        if (GetString(album, "id") is not { } id || GetString(album, "name") is not { } name)
            return null;
        var releaseDate = GetString(album, "release_date");
        var releaseYear = releaseDate is { Length: >= 4 } ? releaseDate[..4] : releaseDate;
        return new LibraryAlbum(
            id,
            name,
            string.Join(", ", Names(album, "artists")),
            (int)Math.Max(0, GetInt64(album, "total_tracks")),
            releaseYear,
            $"https://open.spotify.com/album/{id}",
            LibraryImage(album));
    }

    private static LibraryTrack? ParseTrackObject(
        JsonElement track,
        bool entryLocal,
        string? fallbackAlbumId = null,
        string? fallbackAlbumName = null,
        string? fallbackAlbumImage = null,
        string? subtitle = null,
        bool allowMissingType = false)
    {
        var type = GetString(track, "type");
        if ((allowMissingType ? type is not null && type != "track" : type != "track")
            || GetString(track, "name") is not { } name)
            return null;

        var uri = GetString(track, "uri") ?? string.Empty;
        var spotifyUrl = SongBanner.SpotifyUrl(uri, PlaybackSourcePreference.Soloist);
        var canPlay = spotifyUrl is not null && !entryLocal && !GetBoolean(track, "is_local")
            && !(track.TryGetProperty("is_playable", out var playable) && playable.ValueKind == JsonValueKind.False)
            && !(track.TryGetProperty("restrictions", out var restrictions) && restrictions.ValueKind == JsonValueKind.Object
                && GetString(restrictions, "reason") is not null);

        string? albumName = fallbackAlbumName;
        string? albumId = fallbackAlbumId;
        string? cover = fallbackAlbumImage;
        if (track.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName ??= GetString(album, "name");
            albumId ??= GetString(album, "id");
            cover ??= LibraryImage(album);
        }
        var albumUrl = albumId is { Length: > 0 } ? $"https://open.spotify.com/album/{albumId}" : null;

        return new LibraryTrack(
            uri,
            name,
            string.Join(", ", Names(track, "artists")),
            GetInt64(track, "duration_ms"),
            spotifyUrl,
            canPlay,
            cover,
            albumName,
            albumId,
            albumUrl,
            subtitle);
    }

    private static string? CleanPlaylistDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var stripped = Regex.Replace(raw, "<.*?>", string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped).Trim();
        return decoded.Length == 0 ? null : decoded;
    }

    private static string? FormatPlayedAt(string? isoTimestamp, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var playedAt))
            return null;
        var elapsed = now - playedAt;
        if (elapsed.TotalMinutes < 1) return "刚刚";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} 分钟前";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} 小时前";
        if (elapsed.TotalDays < 2) return "昨天";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} 天前";
        return playedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string? LibraryImage(JsonElement item)
    {
        var url = ImageUrl(item);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    internal async Task WaitForLibraryAsync()
    {
        await _playerGate.WaitAsync();
        _playerGate.Release();
    }

    private async Task<bool> CanReadPlaylistAsync(string id, CancellationToken token)
    {
        try
        {
            using var page = await ReadLibraryPageAsync($"/playlists/{Uri.EscapeDataString(id)}/items", 0, token, 1);
            return true;
        }
        catch (LibraryRequestException exception) when (
            exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private Task<JsonDocument> ReadLibraryPageAsync(string path, int offset, CancellationToken token, int limit = LibraryPageSize) =>
        ReadLibraryJsonAsync(FormattableString.Invariant($"{path}?limit={limit}&offset={offset}"), true, token);

    private async Task<JsonDocument?> SendLibraryJsonAsync(HttpMethod method, string path, CancellationToken token)
    {
        using var response = await SendLibraryRequestAsync(method, path, token, throwOnError: false);
        if (response is null)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private async Task<HttpResponseMessage?> SendLibraryRequestAsync(
        HttpMethod method, string path, CancellationToken token, bool throwOnError = true)
    {
        await _playerGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_credentials.ClientId.Length == 0 || _credentials.ClientSecret.Length == 0)
            {
                if (!throwOnError) return null;
                throw new InvalidOperationException("请在设置中切换到 Spotify，填写并保存 Client ID 和 Client Secret，再切回 Soloist。");
            }

            _tokens ??= await LoadTokenAsync(token);
            if (!HasScope(LibraryReadScope) || !HasScope(LibraryModifyScope) || !HasScope(PlaylistReadScope) || !HasScope(CollaborativeScope))
            {
                if (!throwOnError) return null;
                _tokens = await AuthorizeLibraryAsync(token);
            }
            else if (_tokens!.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            {
                _tokens = await RefreshLibraryAsync(token);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(method, "https://api.spotify.com/v1" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
                var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                    return response;

                var status = response.StatusCode;
                var retryAfter = (int)status == 429 ? RetryAfter(response) : default;
                response.Dispose();
                if (status == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _tokens = await RefreshLibraryAsync(token);
                    continue;
                }
                if (!throwOnError)
                    return null;
                throw new InvalidOperationException(status switch
                {
                    HttpStatusCode.Forbidden => "Spotify 拒绝操作，请检查账号权限或应用设置",
                    HttpStatusCode.Unauthorized => "Spotify 库授权已失效，请重试",
                    (HttpStatusCode)429 => $"Spotify 请求过于频繁，请在 {Math.Ceiling(retryAfter.TotalSeconds):0} 秒后重试",
                    _ => $"Spotify 请求失败（HTTP {(int)status}）"
                });
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && throwOnError)
        {
            throw new InvalidOperationException("Spotify 请求超时，请重试");
        }
        catch (HttpRequestException exception) when (throwOnError)
        {
            throw new InvalidOperationException("无法连接 Spotify，请检查网络后重试", exception);
        }
        finally
        {
            _playerGate.Release();
        }
    }

    private async Task<JsonDocument> ReadLibraryJsonAsync(string path, bool paged, CancellationToken token)
    {
        await _playerGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_credentials.ClientId.Length == 0 || _credentials.ClientSecret.Length == 0)
                throw new InvalidOperationException("请在设置中切换到 Spotify，填写并保存 Client ID 和 Client Secret，再切回 Soloist。");

            _tokens ??= await LoadTokenAsync(token);
            if (!HasScope(LibraryReadScope) || !HasScope(PlaylistReadScope) || !HasScope(CollaborativeScope))
                _tokens = await AuthorizeLibraryAsync(token);
            else if (_tokens!.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                _tokens = await RefreshLibraryAsync(token);
            if (!HasScope(LibraryReadScope) || !HasScope(PlaylistReadScope) || !HasScope(CollaborativeScope))
                throw new InvalidOperationException("Spotify 未授予读取收藏和歌单的权限，请重新授权");

            try
            {
                return await RequestAsync();
            }
            catch (LibraryRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens = await RefreshLibraryAsync(token);
                return await RequestAsync();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Spotify 请求超时，请重试");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("无法连接 Spotify，请检查网络后重试", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Spotify 返回的库数据无法读取，请重试", exception);
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            throw new InvalidOperationException("无法打开 Spotify 授权回调，请检查本机 43821 端口", exception);
        }
        catch (TokenEndpointException exception)
        {
            throw new InvalidOperationException("Spotify 授权失败，请检查设置中的 Spotify 参数", exception);
        }
        finally
        {
            _playerGate.Release();
        }

        async Task<JsonDocument> RequestAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden when path.StartsWith("/playlists/", StringComparison.Ordinal) =>
                        "Spotify 已拒绝读取此歌单，返回库后刷新列表",
                    HttpStatusCode.Forbidden when path.StartsWith("/me/player/recently-played", StringComparison.Ordinal) =>
                        "Spotify 未授予最近播放权限，请在设置中重新授权",
                    HttpStatusCode.Forbidden => "Spotify 拒绝访问库，请检查账号是否有权使用此开发应用",
                    HttpStatusCode.Unauthorized => "Spotify 库授权已失效，请重试",
                    (HttpStatusCode)429 => $"Spotify 请求过于频繁，请在 {Math.Ceiling(RetryAfter(response).TotalSeconds):0} 秒后重试",
                    _ => $"读取 Spotify 库失败（HTTP {(int)response.StatusCode}）"
                };
                throw new LibraryRequestException(response.StatusCode, message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && (!paged || json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array))
                return json;

            json.Dispose();
            throw new InvalidOperationException("Spotify 返回的库数据格式无效");
        }
    }

    private async Task<TokenState> AuthorizeLibraryAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        return await AuthorizeAsync(LibraryScope, showPlaybackStatus: false, timeout.Token);
    }

    private async Task<TokenState> RefreshLibraryAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await RefreshAsync(timeout.Token);
        }
        catch (TokenEndpointException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            return await AuthorizeLibraryAsync(token);
        }
    }

    private static int? NextLibraryOffset(JsonElement root, int currentOffset)
    {
        if (!Uri.TryCreate(GetString(root, "next"), UriKind.Absolute, out var next))
            return null;
        var total = GetNullableInt64(root, "total");
        foreach (var field in next.Query.TrimStart('?').Split('&'))
        {
            if (field.StartsWith("offset=", StringComparison.Ordinal)
                && int.TryParse(field.AsSpan(7), CultureInfo.InvariantCulture, out var offset)
                && offset > currentOffset
                && (total is null || offset < total))
                return offset;
        }
        return null;
    }

    private sealed class LibraryRequestException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
