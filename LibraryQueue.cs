namespace Kashishu;

internal enum LibrarySortMode
{
    Default,
    Title,
    Artist,
    Duration
}

internal static class LibraryTrackQuery
{
    public static IReadOnlyList<LibraryTrack> Apply(
        IReadOnlyList<LibraryTrack> source, string? filter, LibrarySortMode sort)
    {
        IEnumerable<LibraryTrack> query = source;
        var needle = filter?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            query = query.Where(track =>
                track.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || track.Artists.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (track.Album?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        query = sort switch
        {
            LibrarySortMode.Title => query.OrderBy(track => track.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortMode.Artist => query.OrderBy(track => track.Artists, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(track => track.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortMode.Duration => query.OrderBy(track => track.DurationMs),
            _ => query
        };

        return query is IReadOnlyList<LibraryTrack> list ? list : query.ToList();
    }

    public static string TrackIdFromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return string.Empty;
        const string prefix = "spotify:track:";
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    public static bool MatchesTrack(string? trackId, string? uri) =>
        !string.IsNullOrEmpty(trackId)
        && !string.IsNullOrEmpty(uri)
        && (uri == trackId || string.Equals(TrackIdFromUri(uri), trackId, StringComparison.Ordinal));
}

/// <summary>Maintains an optional continuous or shuffled playback queue for tracks started from the Library.</summary>
internal sealed class LibraryQueue
{
    private readonly List<LibraryTrack> _items = [];
    private int _index = -1;
    private bool _sawExpectedPlaying;
    private double _lastObservedPositionMs;
    private DateTimeOffset _startedAt;
    private bool _advancing;

    public bool IsActive => _index >= 0 && _index < _items.Count;
    public bool IsShuffled { get; private set; }
    public string SourceTitle { get; private set; } = string.Empty;
    public LibraryTrack? Current => IsActive ? _items[_index] : null;
    public int Count => _items.Count;
    public int Position => IsActive ? _index + 1 : 0;
    public bool CanPrevious => IsActive && _index > 0;
    public bool CanNext => IsActive && _index + 1 < _items.Count;

    public event Action? Changed;

    public LibraryTrack? Start(
        string sourceTitle,
        IReadOnlyList<LibraryTrack> tracks,
        LibraryTrack? startTrack,
        bool shuffle,
        Random? random = null)
    {
        var playable = tracks.Where(track => track.CanPlay).ToList();
        if (playable.Count == 0)
        {
            Stop();
            return null;
        }

        _items.Clear();
        IsShuffled = shuffle;
        SourceTitle = sourceTitle;

        if (shuffle)
        {
            var rng = random ?? Random.Shared;
            var remaining = playable.Where(t => !ReferenceEquals(t, startTrack) && t.Uri != startTrack?.Uri).ToList();
            for (var i = remaining.Count - 1; i > 0; i--)
            {
                var swap = rng.Next(i + 1);
                (remaining[i], remaining[swap]) = (remaining[swap], remaining[i]);
            }
            if (startTrack is { CanPlay: true })
                _items.Add(startTrack);
            _items.AddRange(remaining);
            _index = 0;
        }
        else
        {
            _items.AddRange(playable);
            _index = startTrack is null
                ? 0
                : Math.Max(0, _items.FindIndex(t => ReferenceEquals(t, startTrack) || t.Uri == startTrack.Uri));
        }

        ResetTrackObservation();
        Changed?.Invoke();
        return Current;
    }

    public LibraryTrack? MoveNext()
    {
        if (!CanNext)
        {
            Stop();
            return null;
        }
        _index++;
        ResetTrackObservation();
        Changed?.Invoke();
        return Current;
    }

    public LibraryTrack? MovePrevious()
    {
        if (!CanPrevious)
            return null;
        _index--;
        ResetTrackObservation();
        Changed?.Invoke();
        return Current;
    }

    public void ToggleShuffle(Random? random = null)
    {
        if (!IsActive || Current is not { } current)
            return;

        IsShuffled = !IsShuffled;
        if (IsShuffled)
        {
            var rng = random ?? Random.Shared;
            var rest = _items.Where((_, idx) => idx != _index).ToList();
            for (var i = rest.Count - 1; i > 0; i--)
            {
                var swap = rng.Next(i + 1);
                (rest[i], rest[swap]) = (rest[swap], rest[i]);
            }
            _items.Clear();
            _items.Add(current);
            _items.AddRange(rest);
            _index = 0;
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive && _items.Count == 0)
            return;
        _items.Clear();
        _index = -1;
        IsShuffled = false;
        SourceTitle = string.Empty;
        _advancing = false;
        Changed?.Invoke();
    }

    public void MarkAdvancing(bool advancing) => _advancing = advancing;

    /// <summary>Inspects playback updates to advance cleanly when a queued track finishes, or stops on external track change.</summary>
    public LibraryTrack? ObserveSnapshot(PlaybackSnapshot snapshot)
    {
        if (!IsActive || _advancing || Current is not { } current)
            return null;

        var matching = LibraryTrackQuery.MatchesTrack(snapshot.Track?.Id, current.Uri);
        if (matching)
        {
            if (snapshot.IsPlaying)
            {
                _sawExpectedPlaying = true;
                _lastObservedPositionMs = Math.Max(_lastObservedPositionMs, snapshot.PositionMs);
            }
            else if (_sawExpectedPlaying
                     && current.DurationMs > 3000
                     && _lastObservedPositionMs >= current.DurationMs - 2500
                     && snapshot.PositionMs >= current.DurationMs - 1500)
            {
                return MoveNext();
            }
            return null;
        }

        // Allow brief transition grace right after issuing PlayTrackAsync before external-change cancellation kicks in.
        if (DateTimeOffset.UtcNow - _startedAt < TimeSpan.FromSeconds(3.5))
            return null;

        if (snapshot.Track is null)
        {
            if (_sawExpectedPlaying
                && (current.DurationMs <= 3000 || _lastObservedPositionMs >= current.DurationMs - 3000))
            {
                return MoveNext();
            }
            return null;
        }

        // User switched to an unrelated track outside the Library queue.
        Stop();
        return null;
    }

    private void ResetTrackObservation()
    {
        _sawExpectedPlaying = false;
        _lastObservedPositionMs = 0;
        _startedAt = DateTimeOffset.UtcNow;
        _advancing = false;
    }
}
