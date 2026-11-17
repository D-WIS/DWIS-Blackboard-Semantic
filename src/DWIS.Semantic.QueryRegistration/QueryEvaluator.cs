using DWIS.Semantic.Store;
using Microsoft.Extensions.Logging;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>Answers one registered query; null when the query could not be answered (the last answer is then kept).</summary>
public delegate Task<QueryRows?> QueryEvaluator(string query, CancellationToken cancellationToken);

/// <summary>
/// Starts a round: gives the evaluator that answers every query of the round. The old server copied the
/// ontology once per round here, because the copy was nearly all a query cost; against a store there is
/// nothing to copy, and the hook stays so that a round can one day pin one state of the store for all its
/// queries (R-QR-2).
/// </summary>
public delegate Task<QueryEvaluator> RoundStarter(CancellationToken cancellationToken);

/// <summary>
/// The evaluator of the registered queries against the live dataset of the store (specification 3.5): a
/// SELECT under the limits of R-SEM-8. An answer the limits cut short is no answer (null), so that no row is
/// ever reported removed because the store stopped listing it; a query that is not a SELECT is a failure too.
/// </summary>
public sealed class StoreQueryEvaluator
{
    private readonly ITripleStore _store;
    private readonly QueryOptions _options;
    private readonly ILogger<StoreQueryEvaluator>? _logger;

    /// <summary>Creates the evaluator over the store with the limits of the host.</summary>
    public StoreQueryEvaluator(ITripleStore store, QueryOptions? options = null, ILogger<StoreQueryEvaluator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _options = options ?? QueryOptions.Default;
        _logger = logger;
    }

    /// <summary>Runs the query on the live dataset.</summary>
    public async Task<QueryRows?> EvaluateAsync(string query, CancellationToken cancellationToken)
    {
        var result = await _store.QueryAsync(Dataset.Live, query, _options, cancellationToken).ConfigureAwait(false);
        if (result.Table is null || result.Table.ResultsType != VDS.RDF.Query.SparqlResultsType.VariableBindings)
        {
            if (_logger is not null)
            {
                Log.NotASelect(_logger);
            }
            return null;
        }

        if (result.Truncated)
        {
            if (_logger is not null)
            {
                Log.AnswerCut(_logger);
            }
            return null;
        }

        return QueryRows.FromResultSet(result.Table);
    }

    /// <summary>The evaluator as the manager takes it.</summary>
    public QueryEvaluator AsDelegate() => EvaluateAsync;
}
