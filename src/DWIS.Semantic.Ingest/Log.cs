using Microsoft.Extensions.Logging;

namespace DWIS.Semantic.Ingest;

/// <summary>The log messages of the pipeline (source-generated, CA1848).</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "An observer failed after {Kind} of {Count} manifest(s); the change itself is written.")]
    public static partial void ObserverFailed(ILogger logger, Exception exception, DescriptionChangeKind kind, int count);
}
