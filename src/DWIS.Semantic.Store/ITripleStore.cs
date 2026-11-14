using VDS.RDF;

namespace DWIS.Semantic.Store;

/// <summary>The datasets of the store (specification 3.7). The implementation maps each to a path.</summary>
public enum Dataset
{
    /// <summary>The live data: vocabulary, units, and per manifest the asserted, inferred, provenance and endpoints graphs. Queries run here.</summary>
    Live,

    /// <summary>The whole manifest graph of a rejected injection, invisible to live queries (Appendix C.5).</summary>
    Quarantine,

    /// <summary>The inference view over the live data, used by the ingest step only (milestone 2).</summary>
    Inference,
}

/// <summary>
/// What the semantic service needs from a triple store, and nothing more (specification 3.2): named
/// graphs read and written whole, SPARQL query with a time and row limit (R-SEM-8), SPARQL update, SHACL
/// validation of one graph, and a health check. Fuseki first (<see cref="FusekiStore"/>); another store
/// implements this interface. Graph names are absolute IRIs as strings, canonical namespace (R-SEM-9).
/// </summary>
public interface ITripleStore
{
    /// <summary>True when the store answers its health endpoint.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the named graph with the content (creates it when absent).</summary>
    Task PutGraphAsync(Dataset dataset, string graph, IGraph content, CancellationToken cancellationToken = default);

    /// <summary>Adds the content to the named graph (creates it when absent).</summary>
    Task AddToGraphAsync(Dataset dataset, string graph, IGraph content, CancellationToken cancellationToken = default);

    /// <summary>The named graph, or null when it does not exist.</summary>
    Task<IGraph?> GetGraphAsync(Dataset dataset, string graph, CancellationToken cancellationToken = default);

    /// <summary>Deletes the named graph; false when it did not exist.</summary>
    Task<bool> DeleteGraphAsync(Dataset dataset, string graph, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a read-only SPARQL 1.1 query under the time and row limits of the options (R-SEM-8). SELECT and
    /// ASK fill <see cref="QueryResult.Table"/>, CONSTRUCT and DESCRIBE fill <see cref="QueryResult.Graph"/>.
    /// Throws <see cref="StoreTimeoutException"/> when the time limit is hit before the first row; a limit hit
    /// while rows are being read, or the row limit, truncates the answer and sets <see cref="QueryResult.Truncated"/>.
    /// </summary>
    Task<QueryResult> QueryAsync(Dataset dataset, string sparql, QueryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a SPARQL 1.1 Update request as one transaction.</summary>
    Task UpdateAsync(Dataset dataset, string sparqlUpdate, CancellationToken cancellationToken = default);

    /// <summary>Validates one named graph against the shapes and returns the SHACL validation report graph.</summary>
    Task<IGraph> ValidateAsync(Dataset dataset, string graph, IGraph shapes, CancellationToken cancellationToken = default);
}
