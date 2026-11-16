using System.Text.Json;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;
using Microsoft.AspNetCore.Http.HttpResults;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DWIS.Semantic.Host.Endpoints;

/// <summary>What <c>GET /api/v1/vocabulary</c> answers.</summary>
/// <param name="Version">The configured version.</param>
/// <param name="Graph">The vocabulary graph IRI.</param>
/// <param name="UnitsGraph">The units graph IRI.</param>
/// <param name="Loaded">True once the model is built.</param>
/// <param name="LoadedAt">When it was read from the store.</param>
/// <param name="Triples">Triples in the vocabulary graph.</param>
/// <param name="UnitTriples">Triples in the units graph.</param>
/// <param name="PolicyFile">The validation policy file in force, or empty.</param>
/// <param name="PolicyRules">Entries in the policy.</param>
/// <param name="Error">Why the vocabulary is not loaded, or null.</param>
public sealed record VocabularyAnswer(
    string Version,
    string Graph,
    string UnitsGraph,
    bool Loaded,
    DateTimeOffset? LoadedAt,
    int Triples,
    int UnitTriples,
    string PolicyFile,
    int PolicyRules,
    string? Error);

/// <summary>What <c>PUT /api/v1/vocabulary</c> takes: the version whose graphs are already in the store.</summary>
/// <param name="Version">The version; the graphs <c>http://d-wis.org/vocabulary/{version}</c> and <c>http://d-wis.org/units/{version}</c> must exist.</param>
public sealed record VocabularyChange(string Version);

/// <summary>One manifest in the answer of <c>PUT /api/v1/vocabulary</c>.</summary>
/// <param name="ManifestId">The manifest.</param>
/// <param name="AcceptedBefore">Whether it was live whole before.</param>
/// <param name="Accepted">Whether it is live whole now.</param>
/// <param name="Errors">Errors in the new report.</param>
/// <param name="Warnings">Warnings in the new report.</param>
/// <param name="Inferred">Triples in the new inferred graph.</param>
public sealed record RederivedAnswer(string ManifestId, bool AcceptedBefore, bool Accepted, int Errors, int Warnings, int Inferred);

/// <summary>What <c>PUT /api/v1/vocabulary</c> answers.</summary>
/// <param name="Version">The version in force now.</param>
/// <param name="PreviousVersion">The version before; its graphs are dropped when it differs.</param>
/// <param name="Manifests">Every live manifest, re-derived.</param>
/// <param name="BecameCoreOnly">The manifests that were live whole and are core-only now: the reason to load the previous version again and put it back.</param>
public sealed record VocabularyChangeAnswer(string Version, string PreviousVersion, IReadOnlyList<RederivedAnswer> Manifests, IReadOnlyList<string> BecameCoreOnly);

/// <summary>
/// <c>GET /api/v1/vocabulary</c> (the version in force and its graphs, specification 3.4), <c>PUT
/// /api/v1/vocabulary</c> (switch to a version whose graphs the loader has put in the store, drop the
/// previous version's graphs so that the inference view reasons over one vocabulary, re-derive every live
/// manifest, Appendix C.7) and <c>GET /api/v1/health</c>: healthy when the store answers and the
/// vocabulary is loaded, else 503 with the reason per check.
/// </summary>
public static class ServiceEndpoints
{
    private static readonly JsonSerializerOptions HealthJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim OneChangeAtATime = new(1, 1);

    /// <summary>Maps the routes.</summary>
    public static RouteGroupBuilder MapService(this RouteGroupBuilder api)
    {
        api.MapPut("/vocabulary", ChangeVocabularyAsync);
        api.MapGet("/vocabulary", (VocabularyState state) =>
            TypedResults.Ok(new VocabularyAnswer(
                state.Version,
                state.Graph,
                state.UnitsGraph,
                state.Loaded,
                state.LoadedAt,
                state.Triples,
                state.UnitTriples,
                state.PolicyFile,
                state.Policy.Rules.Count,
                state.Loaded ? null : state.Error)));

        api.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthAsync,
        });
        return api;
    }

    private static async Task<Results<Ok<VocabularyChangeAnswer>, ProblemHttpResult>> ChangeVocabularyAsync(VocabularyChange change, VocabularyState state, VocabularyLoader loader, IngestPipeline pipeline, ITripleStore store, ILogger<VocabularyLoader> logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(change?.Version) || change.Version.Contains('/', StringComparison.Ordinal))
        {
            return TypedResults.Problem("Give the version as {\"version\": \"…\"}; it names the graphs http://d-wis.org/vocabulary/{version} and http://d-wis.org/units/{version}, which must be in the store.", statusCode: StatusCodes.Status400BadRequest, title: "No version");
        }

        if (!await OneChangeAtATime.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.Problem("A vocabulary change is in progress.", statusCode: StatusCodes.Status409Conflict, title: "Busy");
        }

        try
        {
            var previous = state.Version;
            if (!await loader.SwitchAsync(change.Version, cancellationToken).ConfigureAwait(false))
            {
                return TypedResults.Problem(state.Error ?? "The version's graphs are not in the store.", statusCode: StatusCodes.Status404NotFound, title: "Vocabulary not found");
            }

            if (previous != change.Version)
            {
                // One vocabulary at a time in the union the inference view reasons over.
                await store.DeleteGraphAsync(Dataset.Live, VocabularyOptions.GraphOf(previous), cancellationToken).ConfigureAwait(false);
                await store.DeleteGraphAsync(Dataset.Live, VocabularyOptions.UnitsGraphOf(previous), cancellationToken).ConfigureAwait(false);
            }

            var rederived = await pipeline.RederiveAsync(state.Model!, state.Policy, cancellationToken).ConfigureAwait(false);
            var answer = new VocabularyChangeAnswer(
                change.Version,
                previous,
                rederived.Select(r => new RederivedAnswer(r.Id.Value, r.AcceptedBefore, r.Report.Accepted, r.Report.Counts.Errors, r.Report.Counts.Warnings, r.Report.Statistics.Inferred)).ToList(),
                rederived.Where(r => r.BecameCoreOnly).Select(r => r.Id.Value).ToList());
            Log.VocabularyChanged(logger, change.Version, previous, answer.Manifests.Count, answer.BecameCoreOnly.Count);
            return TypedResults.Ok(answer);
        }
        catch (StoreException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }
        finally
        {
            OneChangeAtATime.Release();
        }
    }

    /// <summary>Registers the two checks.</summary>
    public static IHealthChecksBuilder AddSemanticHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<StoreHealthCheck>("store")
            .AddCheck<VocabularyHealthCheck>("vocabulary");

    private static Task WriteHealthAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var answer = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(answer, HealthJson));
    }

    private sealed class StoreHealthCheck(ITripleStore store) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await store.PingAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("The store answers.")
                : HealthCheckResult.Unhealthy("The store does not answer its ping.");
    }

    private sealed class VocabularyHealthCheck(VocabularyState state) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(state.Loaded
                ? HealthCheckResult.Healthy($"Vocabulary loaded at {state.LoadedAt:O}.")
                : HealthCheckResult.Unhealthy(state.Error ?? "The vocabulary is not loaded yet."));
    }
}
