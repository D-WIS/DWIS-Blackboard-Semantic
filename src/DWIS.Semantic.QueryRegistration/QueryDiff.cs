using System.Collections.Immutable;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>
/// What changed in a registered query's answer: the rows added and the rows removed (R-QR-1). A first answer
/// is a diff from nothing: every row is added. Rows are compared by value (<see cref="QueryRow"/>); the order
/// of the rows carries no meaning.
/// </summary>
public sealed class QueryDiff
{
    private QueryDiff(int queryId, ImmutableArray<string> variables, ImmutableArray<QueryRow> added, ImmutableArray<QueryRow> removed)
    {
        QueryId = queryId;
        Variables = variables;
        Added = added;
        Removed = removed;
    }

    /// <summary>The registered query.</summary>
    public int QueryId { get; }

    /// <summary>The variables of the rows.</summary>
    public ImmutableArray<string> Variables { get; }

    /// <summary>The rows that are in the new answer and were not in the old one.</summary>
    public ImmutableArray<QueryRow> Added { get; }

    /// <summary>The rows that were in the old answer and are not in the new one.</summary>
    public ImmutableArray<QueryRow> Removed { get; }

    /// <summary>True when nothing changed.</summary>
    public bool IsEmpty => Added.IsEmpty && Removed.IsEmpty;

    /// <summary>The diff from an old answer (null: nothing, so a full snapshot) to a new one.</summary>
    public static QueryDiff Between(int queryId, QueryRows? before, QueryRows after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null)
        {
            return new QueryDiff(queryId, after.Variables, after.Rows.Distinct().ToImmutableArray(), []);
        }

        var old = before.Rows.ToHashSet();
        var current = after.Rows.ToHashSet();
        return new QueryDiff(queryId, after.Variables, current.Where(r => !old.Contains(r)).ToImmutableArray(), old.Where(r => !current.Contains(r)).ToImmutableArray());
    }
}

/// <summary>The outcome of a round: the non-empty diffs of the registered queries whose answer changed.</summary>
public sealed class RoundResult
{
    /// <summary>Creates the outcome.</summary>
    public RoundResult(IEnumerable<QueryDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        Diffs = [.. diffs];
    }

    /// <summary>Nothing changed, or nothing registered.</summary>
    public static RoundResult Empty { get; } = new([]);

    /// <summary>The diffs, one per query whose answer changed.</summary>
    public ImmutableArray<QueryDiff> Diffs { get; }
}
