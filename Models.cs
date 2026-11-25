using System.Collections.Immutable;

namespace Kashishu;

public sealed record LyricSyllable(string Text, long StartMs, long EndMs);

public sealed record LyricLine(
    string Text,
    string? Translation,
    long StartMs,
    long EndMs,
    ImmutableArray<LyricSyllable> Syllables)
{
    public bool IsAiTranslation { get; init; }
}

public sealed record TrackInfo(
    string Id,
    string Title,
    ImmutableArray<string> Artists,
    string Album,
    long DurationMs,
    ImmutableArray<byte> AlbumArtBytes,
    ImmutableArray<LyricLine> Lyrics)
{
    public ImmutableArray<ImmutableArray<byte>> ArtistImageBytes { get; init; } = [];
    public string? AlbumArtUrl { get; init; }
    public string? ShareUrl { get; init; }
    public LyricsSourcePreference? LyricsSource { get; init; }
}

public sealed record PlaybackSnapshot(
    TrackInfo? Track,
    long ReportedPositionMs,
    long ReportedAtTimestamp,
    bool IsPlaying,
    string Status,
    double PlaybackRate = 1d)
{
    public PlaybackControls Controls { get; init; } = new();

    public double PositionMs => Math.Clamp(
        ReportedPositionMs + (IsPlaying
            ? System.Diagnostics.Stopwatch.GetElapsedTime(ReportedAtTimestamp).TotalMilliseconds * PlaybackRate
            : 0),
        0, Track is { DurationMs: > 0 } track ? track.DurationMs : double.MaxValue);
}

internal sealed record SourceTrack(
    string Id,
    string Title,
    ImmutableArray<string> Artists,
    ImmutableArray<string> ArtistIds,
    ImmutableArray<string> AlbumArtists,
    string Album,
    long DurationMs,
    string? Isrc,
    string? AlbumArtUrl)
{
    public string? ShareUrl { get; init; }
}

internal interface IPlaybackSource
{
    Task RunAsync(CancellationToken cancellationToken);
    void SetLyricsSource(LyricsSourcePreference source);
    bool RequestCurrentTrackRefresh();
    Task<string?> ControlAsync(PlaybackCommand command, CancellationToken cancellationToken);
}

internal enum PlaybackAction { PlayPause, Previous, Next, Seek, Shuffle, Repeat, Volume, Favorite }

internal sealed record PlaybackCommand(PlaybackAction Action, double Value = 0, string? TrackId = null);

public sealed record PlaybackControls(
    bool CanPlayPause = false,
    bool CanPrevious = false,
    bool CanNext = false,
    bool CanSeek = false,
    bool? Shuffle = null,
    string? Repeat = null,
    double? Volume = null,
    bool CanFavorite = false,
    bool? IsFavorite = null);

public sealed class PlaybackStore
{
    public const string MissingSpotifyCredentialsStatus = "请到设置内配置 Spotify 参数";
    public const string NoActiveSpotifyPlaybackStatus = "Spotify 当前没有播放";
    public const string NoActiveMprisPlaybackStatus = "MPRIS 当前没有播放";
    public const string NoActiveSoloistPlaybackStatus = "Soloist 当前没有播放";

    internal static bool IsNoActivePlayback(string status) =>
        status is NoActiveSpotifyPlaybackStatus or NoActiveMprisPlaybackStatus or NoActiveSoloistPlaybackStatus;

    internal static bool RequiresAttention(string status) =>
        status == MissingSpotifyCredentialsStatus
        || status.StartsWith("正在连接 Spotify", StringComparison.Ordinal)
        || status.StartsWith("正在等待 Spotify 授权", StringComparison.Ordinal)
        || status.StartsWith("正在连接 MPRIS", StringComparison.Ordinal)
        || !IsNoActivePlayback(status)
            && (status.StartsWith("Spotify ", StringComparison.Ordinal)
                || status.StartsWith("MPRIS ", StringComparison.Ordinal)
                || status.StartsWith("Soloist ", StringComparison.Ordinal));

    private readonly object _updateGate = new();
    private PlaybackSnapshot _snapshot = new(
        null, 0, System.Diagnostics.Stopwatch.GetTimestamp(), false, "正在连接播放信息源…");

    internal Func<TrackInfo?, TrackInfo?>? PrepareTrack { get; set; }

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    public void Update(PlaybackSnapshot snapshot)
    {
        lock (_updateGate)
        {
            var track = PrepareTrack is { } prepare ? prepare(snapshot.Track) : snapshot.Track;
            Volatile.Write(ref _snapshot, ReferenceEquals(track, snapshot.Track) ? snapshot : snapshot with { Track = track });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshTrack()
    {
        var changed = false;
        lock (_updateGate)
        {
            var snapshot = _snapshot;
            var track = PrepareTrack is { } prepare ? prepare(snapshot.Track) : snapshot.Track;
            if (!ReferenceEquals(track, snapshot.Track))
            {
                Volatile.Write(ref _snapshot, snapshot with { Track = track });
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
