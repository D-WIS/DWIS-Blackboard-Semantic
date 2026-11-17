namespace DWIS.Semantic.Host;

/// <summary>The log messages of the host (source-generated, CA1848).</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Effective configuration ({Count} values, secrets shown as {Mask}):\n{Values}")]
    public static partial void EffectiveConfiguration(ILogger logger, int count, string mask, string values);

    [LoggerMessage(2, LogLevel.Information, "Validation policy {File}: {Rules} rules")]
    public static partial void ValidationPolicy(ILogger logger, string file, int rules);

    [LoggerMessage(3, LogLevel.Warning, "No validation policy file configured (Validation:PolicyFile): every rule keeps its designed level")]
    public static partial void NoValidationPolicy(ILogger logger);

    [LoggerMessage(4, LogLevel.Information, "Vocabulary {Version} loaded from the store: {Triples} triples, {Units} unit and quantity triples, {Classes} classes, shapes {Shapes}")]
    public static partial void VocabularyLoaded(ILogger logger, string version, int triples, int units, int classes, string shapes);

    [LoggerMessage(5, LogLevel.Warning, "Vocabulary not loaded, retrying in {Delay}: {Error}")]
    public static partial void VocabularyRetry(ILogger logger, TimeSpan delay, string? error);

    [LoggerMessage(6, LogLevel.Information, "Manifest {Id} injected: accepted {Accepted}, {Errors} errors, {Warnings} warnings, {Asserted} asserted triples, {Preprocessing} ms pre-processing, {Commit} ms commit")]
    public static partial void ManifestInjected(ILogger logger, string id, bool accepted, int errors, int warnings, int asserted, long preprocessing, long commit);

    [LoggerMessage(7, LogLevel.Error, "Manifest {Id} not committed: the store refused the write")]
    public static partial void StoreRefusedWrite(ILogger logger, Exception exception, string id);

    [LoggerMessage(10, LogLevel.Information, "Manifest {Id} prepared over gRPC: accepted {Accepted}, already live {AlreadyLive}, {Variables} variables to create")]
    public static partial void ManifestPrepared(ILogger logger, string id, bool accepted, bool alreadyLive, int variables);

    [LoggerMessage(11, LogLevel.Information, "Query {Id} registered by {Owner}: {Rows} rows in the snapshot")]
    public static partial void QueryRegistered(ILogger logger, int id, string owner, int rows);

    [LoggerMessage(12, LogLevel.Information, "Owner {Owner} released: {Count} queries went with it")]
    public static partial void OwnerReleased(ILogger logger, string owner, int count);

    [LoggerMessage(13, LogLevel.Information, "Watch stream opened by {Watcher}: {Count} subscriptions")]
    public static partial void WatchOpened(ILogger logger, string watcher, int count);

    [LoggerMessage(14, LogLevel.Information, "Watch stream of {Watcher} closed")]
    public static partial void WatchClosed(ILogger logger, string watcher);

    [LoggerMessage(8, LogLevel.Information, "Manifest {Id} removed")]
    public static partial void ManifestRemoved(ILogger logger, string id);

    [LoggerMessage(9, LogLevel.Information, "Vocabulary changed to {Version} from {Previous}: {Manifests} manifests re-derived, {CoreOnly} became core-only")]
    public static partial void VocabularyChanged(ILogger logger, string version, string previous, int manifests, int coreOnly);
}
