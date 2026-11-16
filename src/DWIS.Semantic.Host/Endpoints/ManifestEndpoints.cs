using System.Text.Json;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DWIS.Semantic.Host.Endpoints;

/// <summary>
/// <c>/api/v1/manifests</c> (specification 3.4): <c>POST</c> runs the whole pipeline with no OPC UA
/// endpoints and answers with the report of Appendix C.9 (a manifest whose id is live is replaced);
/// <c>PUT {provider}/{name}</c> does the same for a manifest whose id must match the route (C.7, update);
/// <c>DELETE {provider}/{name}</c> drops its graphs (C.7, remove); <c>GET</c> lists the live manifests
/// from their provenance; <c>GET {provider}/{name}</c> returns a stored report.
/// </summary>
public static class ManifestEndpoints
{
    /// <summary>Maps the routes.</summary>
    public static RouteGroupBuilder MapManifests(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/manifests");
        group.MapPost("/", (HttpRequest request, IngestPipeline pipeline, VocabularyState vocabulary, ILogger<IngestPipeline> logger, CancellationToken cancellationToken) =>
            InjectAsync(request, null, pipeline, vocabulary, logger, cancellationToken));
        group.MapPut("/{provider}/{name}", (string provider, string name, HttpRequest request, IngestPipeline pipeline, VocabularyState vocabulary, ILogger<IngestPipeline> logger, CancellationToken cancellationToken) =>
            InjectAsync(request, $"{provider}/{name}", pipeline, vocabulary, logger, cancellationToken));
        group.MapDelete("/{provider}/{name}", RemoveAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{provider}/{name}", GetAsync);
        return group;
    }

    private static async Task<Results<JsonHttpResult<IngestionReport>, ProblemHttpResult>> InjectAsync(HttpRequest request, string? routeId, IngestPipeline pipeline, VocabularyState vocabulary, ILogger<IngestPipeline> logger, CancellationToken cancellationToken)
    {
        var model = vocabulary.Model;
        if (model is null)
        {
            return TypedResults.Problem(vocabulary.Error ?? "The vocabulary is not loaded yet.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Vocabulary not loaded");
        }

        Blackboard.Contracts.Manifests.Manifest manifest;
        try
        {
            manifest = await ManifestJson.ParseAsync(request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not a manifest");
        }

        if (string.IsNullOrWhiteSpace(manifest.Provider.Name) || string.IsNullOrWhiteSpace(manifest.ManifestName))
        {
            return TypedResults.Problem("A manifest needs a provider name and a manifest name.", statusCode: StatusCodes.Status400BadRequest, title: "Not a manifest");
        }

        ManifestId id;
        try
        {
            id = ManifestId.Of(manifest);
        }
        catch (ArgumentException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not a manifest");
        }

        if (routeId is not null && routeId != id.Value)
        {
            return TypedResults.Problem($"The manifest's id is '{id.Value}', the route says '{routeId}'. A manifest is updated under its own id; use POST or the matching route.", statusCode: StatusCodes.Status400BadRequest, title: "Id mismatch");
        }

        try
        {
            var report = await pipeline.InjectAsync(manifest, model, vocabulary.Policy, cancellationToken).ConfigureAwait(false);
            Log.ManifestInjected(logger, id.Value, report.Accepted, report.Counts.Errors, report.Counts.Warnings, report.Statistics.Asserted, report.Statistics.PreprocessingMs, report.Statistics.CommitMs);
            return TypedResults.Json(report, ReportJson.Options);
        }
        catch (StoreException e)
        {
            Log.StoreRefusedWrite(logger, e, id.Value);
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(string provider, string name, IngestPipeline pipeline, ILogger<IngestPipeline> logger, CancellationToken cancellationToken)
    {
        var id = ManifestId.Parse($"{provider}/{name}");
        if (id is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            if (!await pipeline.RemoveAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return TypedResults.NotFound();
            }

            Log.ManifestRemoved(logger, id.Value);
            return TypedResults.NoContent();
        }
        catch (StoreException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<ManifestSummary>>, ProblemHttpResult>> ListAsync(IngestPipeline pipeline, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await pipeline.ListAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (StoreException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }
    }

    private static async Task<Results<ContentHttpResult, NotFound, ProblemHttpResult>> GetAsync(string provider, string name, IngestPipeline pipeline, CancellationToken cancellationToken)
    {
        var id = ManifestId.Parse($"{provider}/{name}");
        if (id is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var report = await pipeline.GetReportAsync(id, cancellationToken).ConfigureAwait(false);
            return report is null ? TypedResults.NotFound() : TypedResults.Content(ReportJson.Serialize(report), "application/json");
        }
        catch (StoreException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }
    }
}
