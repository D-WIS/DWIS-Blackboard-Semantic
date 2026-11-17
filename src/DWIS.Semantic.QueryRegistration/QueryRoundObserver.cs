using DWIS.Semantic.Ingest;
using Microsoft.Extensions.Logging;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>Where the diffs of a round go: the streams of the real-time processes and the HTTP subscribers (milestone 3, M3.4).</summary>
public interface IQueryDiffSink
{
    /// <summary>Takes one diff of one round.</summary>
    Task PublishAsync(QueryDiff diff, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a round of the registered queries after every change to the live description (R-QR-2) and hands each
/// diff to every sink. A sink that throws is logged and the other sinks still get the diff.
/// </summary>
public sealed class QueryRoundObserver : ILiveDescriptionObserver
{
    private readonly QueryManager _manager;
    private readonly IReadOnlyList<IQueryDiffSink> _sinks;
    private readonly ILogger<QueryRoundObserver>? _logger;

    /// <summary>Creates the observer over the manager and the sinks.</summary>
    public QueryRoundObserver(QueryManager manager, IEnumerable<IQueryDiffSink>? sinks = null, ILogger<QueryRoundObserver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _sinks = sinks?.ToList() ?? [];
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ChangedAsync(DescriptionChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var round = await _manager.ResolveAllAsync(cancellationToken).ConfigureAwait(false);
        if (_logger is not null)
        {
            Log.Round(_logger, change.Kind, change.Manifests.Count, _manager.Count, round.Diffs.Length);
        }
        foreach (var diff in round.Diffs)
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.PublishAsync(diff, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_logger is not null)
                    {
                        Log.SinkFailed(_logger, ex, diff.QueryId);
                    }
                }
            }
        }
    }
}
