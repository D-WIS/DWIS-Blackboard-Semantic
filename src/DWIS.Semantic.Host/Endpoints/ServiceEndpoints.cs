using System.Text.Json;
using DWIS.Semantic.Store;
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

/// <summary>
/// <c>GET /api/v1/vocabulary</c> (version and graph IRI, specification 3.4; <c>PUT</c> comes with the
/// re-derive step of milestone 2) and <c>GET /api/v1/health</c>: healthy when the store answers and the
/// vocabulary is loaded, else 503 with the reason per check.
/// </summary>
public static class ServiceEndpoints
{
    private static readonly JsonSerializerOptions HealthJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Maps the routes.</summary>
    public static RouteGroupBuilder MapService(this RouteGroupBuilder api)
    {
        api.MapGet("/vocabulary", (VocabularyState state, Microsoft.Extensions.Options.IOptions<VocabularyOptions> options) =>
            TypedResults.Ok(new VocabularyAnswer(
                options.Value.Version,
                options.Value.Graph,
                options.Value.UnitsGraph,
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
