using Microsoft.Extensions.Logging;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>
/// One evaluation per exact query text, shared by its owners (specification 3.5, R-QR-1, R-QR-3). Ported from
/// the old server's <c>QueryManager</c> with the same rules: a query is registered once per text and owned by
/// sessions (or by the service itself, for queries that outlive every session); the last owner to go takes the
/// entry with it; a new subscriber gets a full snapshot, never another subscriber's pending diff; a round
/// answers every registered query once and reports what changed; a query that fails keeps its last answer
/// and blocks nobody; one evaluation runs at a time, so an older answer never replaces a newer one.
/// </summary>
public sealed class QueryManager : IDisposable
{
    private const string InternalOwner = "internal";

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<string, Entry> _byText = new(StringComparer.Ordinal);
    private readonly QueryEvaluator _evaluate;
    private readonly RoundStarter? _beginRound;

    // One evaluation at a time (a round counts as one), so results are stored in the order they were
    // computed: a first answer made during a round waits for the round.
    private readonly SemaphoreSlim _evaluation = new(1, 1);
    private readonly ILogger<QueryManager>? _logger;
    private int _nextId;

    /// <summary>Creates the manager over an evaluator.</summary>
    /// <param name="evaluate">Answers one query.</param>
    /// <param name="beginRound">Optional. Called once per round; the evaluator it returns answers all the queries of that round.</param>
    /// <param name="logger">Optional.</param>
    public QueryManager(QueryEvaluator evaluate, RoundStarter? beginRound = null, ILogger<QueryManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _evaluate = evaluate;
        _beginRound = beginRound;
        _logger = logger;
    }

    /// <summary>The number of distinct queries registered.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Registers a query for an owner (a session id; null for the service itself) and returns its id. The same
    /// text registered again, by the same owner or another, is the same query. Only the ends of the text are
    /// trimmed: literals, comments and inner whitespace are kept as written.
    /// </summary>
    public int Register(string sparql, string? owner = null)
    {
        if (string.IsNullOrWhiteSpace(sparql))
        {
            throw new ArgumentException("A query is required.", nameof(sparql));
        }

        var query = sparql.Trim();
        lock (_gate)
        {
            if (!_byText.TryGetValue(query, out var entry))
            {
                entry = new Entry(checked(++_nextId), query);
                _byText.Add(query, entry);
                _entries.Add(entry.Id, entry);
            }

            entry.Owners.Add(OwnerKey(owner));
            return entry.Id;
        }
    }

    /// <summary>True when the session owns the query.</summary>
    public bool IsOwnedBy(string sparql, string owner)
    {
        ArgumentNullException.ThrowIfNull(sparql);
        lock (_gate)
        {
            return _byText.TryGetValue(sparql.Trim(), out var entry) && entry.Owners.Contains(OwnerKey(owner));
        }
    }

    /// <summary>
    /// Releases a session's ownership of a query. False when the session did not own it. The id says which
    /// query the session released; other owners can keep the query alive.
    /// </summary>
    public bool Unregister(string sparql, string owner, out int? releasedId)
    {
        ArgumentNullException.ThrowIfNull(sparql);
        lock (_gate)
        {
            releasedId = null;
            if (!_byText.TryGetValue(sparql.Trim(), out var entry) || !entry.Owners.Remove(OwnerKey(owner)))
            {
                return false;
            }

            releasedId = entry.Id;
            if (entry.Owners.Count == 0)
            {
                Remove(entry);
            }

            return true;
        }
    }

    /// <summary>Adds an owner to a registered query by its id; false when the id is unknown.</summary>
    public bool AddOwner(int id, string? owner)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return false;
            }

            entry.Owners.Add(OwnerKey(owner));
            return true;
        }
    }

    /// <summary>Releases an owner's registration of a query by its id; false when the id is unknown or the owner did not hold it.</summary>
    public bool Unregister(int id, string? owner)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || !entry.Owners.Remove(OwnerKey(owner)))
            {
                return false;
            }

            if (entry.Owners.Count == 0)
            {
                Remove(entry);
            }

            return true;
        }
    }

    /// <summary>The text of a registered query, or null when the id is unknown.</summary>
    public string? TextOf(int id) => Find(id)?.Query;

    /// <summary>Releases everything a session owns (its close); returns the ids of the queries that went with it.</summary>
    public IReadOnlyList<int> ReleaseOwner(string owner)
    {
        var key = OwnerKey(owner);
        lock (_gate)
        {
            var removed = new List<int>();
            foreach (var entry in _entries.Values.ToArray())
            {
                if (entry.Owners.Remove(key) && entry.Owners.Count == 0)
                {
                    Remove(entry);
                    removed.Add(entry.Id);
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// The full answer for a new subscriber, as a diff from nothing: the stored answer of the query, evaluated
    /// only when the query has none yet. The TLA+ model of M3.0 showed why it is the stored one: a fresh answer
    /// next to an older stored one left the subscriber with a row that no later diff removed, since the diffs
    /// are computed from the stored answer. It never consumes a diff another subscriber has not seen. Null
    /// when the id is unknown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query could not be answered.</exception>
    public async Task<QueryDiff?> SnapshotAsync(int id, CancellationToken cancellationToken = default)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return null;
        }

        await _evaluation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Result ??= await Answer(_evaluate, entry.Query, cancellationToken).ConfigureAwait(false);
            return QueryDiff.Between(id, null, entry.Result);
        }
        finally
        {
            _evaluation.Release();
        }
    }

    /// <summary>
    /// Answers one query again and returns what changed since its stored answer; the stored answer is
    /// replaced unless <paramref name="update"/> is false. Null when the id is unknown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query could not be answered; the stored answer is kept.</exception>
    public async Task<QueryDiff?> ResolveAsync(int id, bool update = true, CancellationToken cancellationToken = default)
    {
        await _evaluation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveCore(id, update, _evaluate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _evaluation.Release();
        }
    }

    /// <summary>
    /// A round: answers every registered query again (R-QR-2) and returns the diffs of those whose answer
    /// changed. A query that fails keeps its last answer and is left out; so is a query whose last owner went
    /// while the round ran. Nothing registered: nothing is evaluated and no round is started.
    /// </summary>
    public async Task<RoundResult> ResolveAllAsync(CancellationToken cancellationToken = default)
    {
        int[] ids;
        lock (_gate)
        {
            ids = [.. _entries.Keys];
        }

        if (ids.Length == 0)
        {
            return RoundResult.Empty;
        }

        await _evaluation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var evaluate = _evaluate;
            if (_beginRound is not null)
            {
                try
                {
                    evaluate = await _beginRound(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same rule as for one failing query: the injection must go on.
                    if (_logger is not null)
                    {
                        Log.RoundNotStartedAsOne(_logger, ex);
                    }
                }
            }

            var diffs = new List<QueryDiff>();
            foreach (var id in ids)
            {
                QueryDiff? diff;
                try
                {
                    diff = await ResolveCore(id, true, evaluate, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failing query must not end the round.
                    if (_logger is not null)
                    {
                        Log.QueryFailed(_logger, ex, id);
                    }
                    continue;
                }

                // An owner can go while the round runs: a removed entry is never reported.
                if (diff is not null && !diff.IsEmpty && Find(id) is not null)
                {
                    diffs.Add(diff);
                }
            }

            return new RoundResult(diffs);
        }
        finally
        {
            _evaluation.Release();
        }
    }

    /// <summary>Releases the evaluation gate; a disposed manager answers no more queries.</summary>
    public void Dispose() => _evaluation.Dispose();

    private static string OwnerKey(string? owner) => owner is null ? InternalOwner : "session:" + owner;

    private static async Task<QueryRows> Answer(QueryEvaluator evaluate, string query, CancellationToken cancellationToken) =>
        await evaluate(query, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Query resolution failed.");

    private async Task<QueryDiff?> ResolveCore(int id, bool update, QueryEvaluator evaluate, CancellationToken cancellationToken)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return null;
        }

        var current = await Answer(evaluate, entry.Query, cancellationToken).ConfigureAwait(false);
        var diff = QueryDiff.Between(entry.Id, entry.Result, current);
        if (update)
        {
            entry.Result = current;
        }

        return diff;
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry.Id);
        _byText.Remove(entry.Query);
    }

    private Entry? Find(int id)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(id);
        }
    }

    private sealed class Entry(int id, string query)
    {
        public int Id { get; } = id;

        public string Query { get; } = query;

        public HashSet<string> Owners { get; } = new(StringComparer.Ordinal);

        public QueryRows? Result { get; set; }
    }
}
