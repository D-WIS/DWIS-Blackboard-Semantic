using Microsoft.Extensions.Logging;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>The log messages of the query registration (source-generated, CA1848).</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The round could not be started as one; each query is answered on its own.")]
    public static partial void RoundNotStartedAsOne(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Registered query {QueryId} failed; its last answer is kept.")]
    public static partial void QueryFailed(ILogger logger, Exception exception, int queryId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "A registered query must be a SELECT; this one is not.")]
    public static partial void NotASelect(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The answer to a registered query was cut by the row or time limit; the last complete answer is kept.")]
    public static partial void AnswerCut(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Round after {Kind} of {Count} manifest(s): {Registered} registered queries, {Changed} changed.")]
    public static partial void Round(ILogger logger, Ingest.DescriptionChangeKind kind, int count, int registered, int changed);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "A sink refused the diff of query {QueryId}.")]
    public static partial void SinkFailed(ILogger logger, Exception exception, int queryId);
}
