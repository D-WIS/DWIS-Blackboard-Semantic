using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DWIS.Semantic.QueryRegistration;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DWIS.Semantic.Host.Endpoints;

/// <summary>What <c>POST /api/v1/queries</c> takes.</summary>
/// <param name="Query">The SPARQL SELECT; <c>http://ddhub.no/</c> IRIs are read as <c>http://d-wis.org/</c>.</param>
public sealed record QueryRegistration(string Query);

/// <summary>A registered query's full answer (the answer of <c>POST /queries</c> and the first event of the stream).</summary>
/// <param name="Id">The query id.</param>
/// <param name="Variables">The variables of the rows.</param>
/// <param name="Rows">One value per variable per row; null for an unbound variable.</param>
public sealed record QueryAnswer(int Id, IReadOnlyList<string> Variables, IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>What changed in a registered query's answer (an event of the stream).</summary>
/// <param name="Id">The query id.</param>
/// <param name="Variables">The variables of the rows.</param>
/// <param name="Added">The rows that are in the answer now and were not before.</param>
/// <param name="Removed">The rows that were in the answer before and are not now.</param>
public sealed record QueryChange(int Id, IReadOnlyList<string> Variables, IReadOnlyList<IReadOnlyList<string?>> Added, IReadOnlyList<IReadOnlyList<string?>> Removed);

/// <summary>
/// <c>/api/v1/queries</c> (specification 3.4, 3.5): registration for tools and tests. <c>POST</c> registers a
/// SELECT for the owner <c>http</c> and answers with the full answer and the id; <c>DELETE {id}</c> releases
/// that owner; <c>GET {id}/events</c> is a server-sent events stream that owns the query while it is open:
/// first the full answer (event <c>snapshot</c>), then every change (event <c>diff</c>) after each commit,
/// remove or re-derive. The real-time process uses the gRPC calls instead (<c>RegisterQuery</c>, <c>WatchQueries</c>).
/// </summary>
public static class QueryEndpoints
{
    /// <summary>The owner of the registrations made with <c>POST /queries</c>.</summary>
    public const string HttpOwner = "http";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the routes.</summary>
    public static RouteGroupBuilder MapQueries(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/queries");
        group.MapPost("/", RegisterAsync);
        group.MapDelete("/{id:int}", (int id, QueryManager manager) =>
            manager.Unregister(id, HttpOwner) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/{id:int}/events", (int id, QueryManager manager, QueryDiffBroadcaster broadcaster, HttpContext context) =>
            manager.TextOf(id) is null
                ? Results.NotFound()
                : TypedResults.ServerSentEvents(EventsAsync(id, manager, broadcaster, context.RequestAborted)));
        return group;
    }

    private static async Task<Results<Ok<QueryAnswer>, ProblemHttpResult>> RegisterAsync(QueryRegistration registration, QueryManager manager, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(registration?.Query))
        {
            return TypedResults.Problem("Give the query as {\"query\": \"SELECT …\"}.", statusCode: StatusCodes.Status400BadRequest, title: "No query");
        }

        var id = manager.Register(SparqlEndpoints.Canonical(registration.Query), HttpOwner);
        try
        {
            var snapshot = (await manager.SnapshotAsync(id, cancellationToken).ConfigureAwait(false))!;
            return TypedResults.Ok(Answer(snapshot));
        }
        catch (InvalidOperationException e)
        {
            manager.Unregister(id, HttpOwner);
            return TypedResults.Problem("The query could not be answered: " + e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Query refused");
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> EventsAsync(int id, QueryManager manager, QueryDiffBroadcaster broadcaster, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The stream owns the query while it is open, under an owner of its own, so that a DELETE of the
        // http owner does not end it and its close does not end the others.
        var owner = "events:" + Guid.NewGuid().ToString("N");
        if (!manager.AddOwner(id, owner))
        {
            yield break;
        }

        using var subscription = broadcaster.Subscribe(d => d.QueryId == id);
        try
        {
            var snapshot = await manager.SnapshotAsync(id, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                yield break;
            }

            yield return new SseItem<string>(JsonSerializer.Serialize(Answer(snapshot), Json), "snapshot");
            await foreach (var diff in subscription.Diffs.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new SseItem<string>(JsonSerializer.Serialize(Change(diff), Json), "diff");
            }
        }
        finally
        {
            manager.Unregister(id, owner);
        }
    }

    private static QueryAnswer Answer(QueryDiff snapshot) => new(snapshot.QueryId, snapshot.Variables, snapshot.Added.Select(Values).ToList());

    private static QueryChange Change(QueryDiff diff) => new(diff.QueryId, diff.Variables, diff.Added.Select(Values).ToList(), diff.Removed.Select(Values).ToList());

    private static IReadOnlyList<string?> Values(QueryRow row) => row.Values;
}
