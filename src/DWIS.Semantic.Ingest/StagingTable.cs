namespace DWIS.Semantic.Ingest;

/// <summary>A staged manifest waiting for its commit (Appendix C.5).</summary>
/// <param name="Manifest">The prepared manifest.</param>
/// <param name="Origin">Where the injection comes from (local, or a hub site), for the provenance.</param>
/// <param name="StagedAt">When it was staged.</param>
/// <param name="ExpiresAt">After this, the entry is dropped and a commit of its id fails.</param>
public sealed record StagedEntry(StagedManifest Manifest, string Origin, DateTimeOffset StagedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The staging table of the two-phase injection (Appendix C.5): what <c>PrepareManifest</c> staged, in memory,
/// until <c>CommitManifest</c> takes it or <c>AbortManifest</c> drops it, with a time limit after which the
/// entry is gone (the real-time process then prepares again). One entry per manifest id: preparing the same
/// id again replaces the entry. Nothing here survives a restart; a manifest that was never committed was
/// never live (R-SEM-7).
/// </summary>
public sealed class StagingTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StagedEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates the table with its time limit.</summary>
    public StagingTable(TimeProvider clock, TimeSpan timeLimit)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (timeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeLimit), "The staging time limit must be positive.");
        }

        _clock = clock;
        TimeLimit = timeLimit;
    }

    /// <summary>How long a staged manifest waits for its commit.</summary>
    public TimeSpan TimeLimit { get; }

    /// <summary>The entries that have not expired.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _entries.Count;
            }
        }
    }

    /// <summary>Stages a prepared manifest; an entry with the same id is replaced.</summary>
    public StagedEntry Stage(StagedManifest manifest, string origin)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var now = _clock.GetUtcNow();
        var entry = new StagedEntry(manifest, origin ?? "", now, now + TimeLimit);
        lock (_gate)
        {
            Sweep();
            _entries[manifest.Id.Value] = entry;
        }

        return entry;
    }

    /// <summary>The entry of an id, when it is staged and not expired; it stays in the table.</summary>
    public StagedEntry? Find(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            Sweep();
            return _entries.GetValueOrDefault(id.Value);
        }
    }

    /// <summary>Takes the entry of an id out of the table for its commit; null when it is not staged or has expired.</summary>
    public StagedEntry? Take(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            Sweep();
            return _entries.Remove(id.Value, out var entry) ? entry : null;
        }
    }

    /// <summary>Drops the entry of an id; false when there was none.</summary>
    public bool Abort(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            Sweep();
            return _entries.Remove(id.Value);
        }
    }

    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var expired in _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
        {
            _entries.Remove(expired);
        }
    }
}
