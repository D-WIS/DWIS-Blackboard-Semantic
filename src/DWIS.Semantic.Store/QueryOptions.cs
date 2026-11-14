using VDS.RDF;
using VDS.RDF.Query;

namespace DWIS.Semantic.Store;

/// <summary>The limits of a query (R-SEM-8). The defaults are those of specification 3.4.</summary>
public sealed record QueryOptions
{
    /// <summary>The defaults: 30 seconds, 10,000 rows or triples.</summary>
    public static QueryOptions Default { get; } = new();

    /// <summary>Time the store may spend on the query before it is cancelled.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Rows (SELECT) or triples (CONSTRUCT, DESCRIBE) kept; the rest is dropped and the answer marked truncated.</summary>
    public int RowLimit { get; init; } = 10_000;
}

/// <summary>The answer to a query: a table for SELECT and ASK, a graph for CONSTRUCT and DESCRIBE.</summary>
public sealed class QueryResult
{
    internal QueryResult(SparqlResultSet? table, IGraph? graph, bool truncated)
    {
        Table = table;
        Graph = graph;
        Truncated = truncated;
    }

    /// <summary>The rows of a SELECT, or the boolean of an ASK (<see cref="SparqlResultSet.Result"/>); null for a graph answer.</summary>
    public SparqlResultSet? Table { get; }

    /// <summary>The triples of a CONSTRUCT or DESCRIBE; null for a table answer.</summary>
    public IGraph? Graph { get; }

    /// <summary>True when the row limit or the time limit cut the answer short.</summary>
    public bool Truncated { get; }
}
