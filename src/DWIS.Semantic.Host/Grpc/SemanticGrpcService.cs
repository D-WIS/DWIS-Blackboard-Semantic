using System.Text.Json;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Host.Endpoints;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.QueryRegistration;
using DWIS.Semantic.Store;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using VDS.RDF;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;
using GrpcManifestSummary = DWIS.Blackboard.Contracts.Grpc.ManifestSummary;
using GrpcProvidedVariable = DWIS.Blackboard.Contracts.Grpc.ProvidedVariable;
using GrpcQueryResult = DWIS.Blackboard.Contracts.Grpc.QueryResult;
using GrpcSemantic = DWIS.Blackboard.Contracts.Grpc.Semantic;

namespace DWIS.Semantic.Host.Grpc;

/// <summary>
/// The <c>Semantic</c> service of <c>proto/semantic.proto</c> (DWIS-Blackboard-Contracts, specification 3.3) as
/// the real-time process calls it: the two-phase injection over the staging table (prepare, commit, abort),
/// remove, the list for the rebuild after a restart, the query and resources facades and the vocabulary.
/// The query registration calls (register, unregister, release, watch) arrive with M3.4 and answer
/// UNIMPLEMENTED until then. Errors are the status codes the proto file names.
/// </summary>
public sealed class SemanticGrpcService : GrpcSemantic.SemanticBase
{
    private readonly IngestPipeline _pipeline;
    private readonly VocabularyState _vocabulary;
    private readonly ITripleStore _store;
    private readonly QueryManager _queries;
    private readonly QueryLimits _limits;
    private readonly ILogger<SemanticGrpcService> _logger;

    /// <summary>Creates the service over the host's parts.</summary>
    public SemanticGrpcService(IngestPipeline pipeline, VocabularyState vocabulary, ITripleStore store, QueryManager queries, IOptions<QueryLimits> limits, ILogger<SemanticGrpcService> logger)
    {
        _pipeline = pipeline;
        _vocabulary = vocabulary;
        _store = store;
        _queries = queries;
        _limits = limits.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<PrepareResult> PrepareManifest(PrepareRequest request, ServerCallContext context)
    {
        var model = _vocabulary.Model ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, _vocabulary.Error ?? "The vocabulary is not loaded yet."));
        Blackboard.Contracts.Manifests.Manifest manifest;
        try
        {
            manifest = ManifestJson.Parse(request.ManifestJson);
        }
        catch (JsonException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Not a manifest: " + e.Message));
        }

        if (string.IsNullOrWhiteSpace(manifest.Provider.Name) || string.IsNullOrWhiteSpace(manifest.ManifestName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A manifest needs a provider name and a manifest name."));
        }

        try
        {
            var outcome = await _pipeline.StageAsync(manifest, model, _vocabulary.Policy, string.IsNullOrWhiteSpace(request.Origin) ? "local" : request.Origin, context.CancellationToken).ConfigureAwait(false);
            Log.ManifestPrepared(_logger, outcome.Id.Value, outcome.Report.Accepted, outcome.AlreadyLive, outcome.Variables.Count);
            var result = new PrepareResult
            {
                Manifest = Ref(outcome.Id),
                Accepted = outcome.Report.Accepted,
                ReportJson = ReportJson.Serialize(outcome.Report),
                ContentHash = outcome.ContentHash,
                AlreadyLive = outcome.AlreadyLive,
            };
            result.Variables.AddRange(outcome.Variables.Select(ToProto));
            return result;
        }
        catch (StoreException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Store error: " + e.Message));
        }
    }

    /// <inheritdoc />
    public override async Task<CommitResult> CommitManifest(CommitRequest request, ServerCallContext context)
    {
        var id = IdOf(request.Manifest);
        var endpoints = request.Endpoints.Select(e => new Ingest.VariableEndpoint(e.VariableId, e.EndpointUrl, e.NamespaceUri, e.NodeId)).ToList();
        try
        {
            var report = await _pipeline.CommitStagedAsync(id, endpoints, context.CancellationToken).ConfigureAwait(false)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"Manifest {id.Value} is not staged: prepare it again (a staged manifest waits {_pipeline.Staging.TimeLimit.TotalSeconds:0} s for its commit)."));
            Log.ManifestInjected(_logger, id.Value, report.Accepted, report.Counts.Errors, report.Counts.Warnings, report.Statistics.Asserted, report.Statistics.PreprocessingMs, report.Statistics.CommitMs);
            return new CommitResult { Manifest = Ref(id), Accepted = report.Accepted, ReportJson = ReportJson.Serialize(report) };
        }
        catch (StoreException e)
        {
            Log.StoreRefusedWrite(_logger, e, id.Value);
            throw new RpcException(new Status(StatusCode.Unavailable, "Store error: " + e.Message));
        }
    }

    /// <inheritdoc />
    public override Task<Empty> AbortManifest(ManifestRef request, ServerCallContext context)
    {
        var id = IdOf(request);
        if (!_pipeline.Abort(id))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Manifest {id.Value} is not staged."));
        }

        return Task.FromResult(new Empty());
    }

    /// <inheritdoc />
    public override async Task<RemoveResult> RemoveManifest(ManifestRef request, ServerCallContext context)
    {
        var id = IdOf(request);
        try
        {
            var removed = await _pipeline.RemoveAsync(id, context.CancellationToken).ConfigureAwait(false);
            if (removed)
            {
                Log.ManifestRemoved(_logger, id.Value);
            }

            return new RemoveResult { Removed = removed };
        }
        catch (StoreException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Store error: " + e.Message));
        }
    }

    /// <inheritdoc />
    public override async Task<ManifestList> ListManifests(Empty request, ServerCallContext context)
    {
        try
        {
            var list = new ManifestList();
            foreach (var summary in await _pipeline.ListAsync(context.CancellationToken).ConfigureAwait(false))
            {
                var entry = new GrpcManifestSummary
                {
                    Manifest = Ref(summary.Id),
                    ManifestName = summary.ManifestName,
                    Accepted = summary.Accepted,
                    ContentHash = summary.ContentHash,
                    VocabularyVersion = summary.VocabularyVersion,
                    InjectedAt = Timestamp.FromDateTimeOffset(summary.InjectedAt),
                };
                var stored = await _pipeline.GetStoredManifestAsync(summary.Id, context.CancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    entry.Variables.AddRange(IngestPipeline.VariablesToCreate(stored).Select(ToProto));
                }

                entry.Endpoints.AddRange((await _pipeline.GetEndpointsAsync(summary.Id, context.CancellationToken).ConfigureAwait(false))
                    .Select(e => new Blackboard.Contracts.Grpc.VariableEndpoint { VariableId = e.VariableId, EndpointUrl = e.EndpointUrl, NamespaceUri = e.NamespaceUri, NodeId = e.NodeId }));
                list.Manifests.Add(entry);
            }

            return list;
        }
        catch (StoreException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Store error: " + e.Message));
        }
    }

    /// <inheritdoc />
    public override async Task<GrpcQueryResult> Query(SparqlRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A query is required."));
        }

        Store.QueryResult result;
        try
        {
            result = await _store.QueryAsync(Dataset.Live, SparqlEndpoints.Canonical(request.Query), _limits.ToQueryOptions(), context.CancellationToken).ConfigureAwait(false);
        }
        catch (StoreTimeoutException e)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, e.Message));
        }
        catch (StoreException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }

        if (result.Table is null || result.Table.ResultsType != VDS.RDF.Query.SparqlResultsType.VariableBindings)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The query must be a SELECT."));
        }

        var rows = QueryRows.FromResultSet(result.Table);
        var answer = new GrpcQueryResult { Truncated = result.Truncated };
        answer.Variables.AddRange(rows.Variables);
        answer.Rows.AddRange(rows.Rows.Select(ToProto));
        return answer;
    }

    /// <inheritdoc />
    public override async Task<ResourceFile> GetResources(Empty request, ServerCallContext context)
    {
        const string individuals = """
            SELECT DISTINCT ?s ?c WHERE {
              GRAPH ?g { ?s a ?c }
              FILTER (STRSTARTS(STR(?g), "http://d-wis.org/manifests/") && !STRENDS(STR(?g), "/inferred") && !STRENDS(STR(?g), "/provenance") && !STRENDS(STR(?g), "/staging"))
              FILTER (isIRI(?s) && isIRI(?c))
            } ORDER BY ?s ?c
            """;
        const string sentences = """
            SELECT DISTINCT ?s ?p ?o WHERE {
              GRAPH ?g { ?s ?p ?o }
              FILTER (STRSTARTS(STR(?g), "http://d-wis.org/manifests/") && !STRENDS(STR(?g), "/inferred") && !STRENDS(STR(?g), "/provenance") && !STRENDS(STR(?g), "/staging"))
              FILTER (isIRI(?s) && isIRI(?o) && ?p != <http://www.w3.org/1999/02/22-rdf-syntax-ns#type>)
            } ORDER BY ?s ?p ?o
            """;
        try
        {
            var options = _limits.ToQueryOptions() with { RowLimit = Math.Max(_limits.RowLimit, 100_000) };
            var file = new ResourceFile();
            var first = await _store.QueryAsync(Dataset.Live, individuals, options, context.CancellationToken).ConfigureAwait(false);
            file.Individuals.AddRange(first.Table!.Results.Select(r => new Individual { Iri = Text(r["s"]), TypeIri = Text(r["c"]) }));
            var second = await _store.QueryAsync(Dataset.Live, sentences, options, context.CancellationToken).ConfigureAwait(false);
            file.Sentences.AddRange(second.Table!.Results.Select(r => new Sentence { Subject = Text(r["s"]), Verb = Text(r["p"]), Object = Text(r["o"]) }));
            return file;
        }
        catch (StoreException e)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Store error: " + e.Message));
        }
    }

    /// <inheritdoc />
    public override Task<VocabularyInfo> GetVocabulary(Empty request, ServerCallContext context) =>
        Task.FromResult(new VocabularyInfo
        {
            Version = _vocabulary.Version,
            VocabularyGraph = _vocabulary.Graph,
            UnitsGraph = _vocabulary.UnitsGraph,
            Loaded = _vocabulary.Loaded,
            RegisteredQueries = _queries.Count,
        });

    private static ManifestRef Ref(ManifestId id) => new() { Provider = id.Provider, Name = id.Name };

    private static ManifestId IdOf(ManifestRef? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Provider) || string.IsNullOrWhiteSpace(reference.Name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A manifest reference needs a provider and a name."));
        }

        return ManifestId.Parse($"{reference.Provider}/{reference.Name}")
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{reference.Provider}/{reference.Name}' is not a manifest id."));
    }

    private static GrpcProvidedVariable ToProto(VariableToCreate variable)
    {
        var result = new GrpcProvidedVariable
        {
            VariableId = variable.VariableId,
            DataType = variable.DataType,
            Rank = variable.Rank,
            InitialValue = variable.InitialValue,
            ValidityVariableId = variable.ValidityVariableId ?? "",
        };
        if (variable.Dimensions is not null)
        {
            result.Dimensions.AddRange(variable.Dimensions);
        }

        return result;
    }

    private static Row ToProto(QueryRow row)
    {
        var result = new Row();
        result.Values.AddRange(row.Values.Select(v => v ?? ""));
        return result;
    }

    private static string Text(INode node) => node switch
    {
        IUriNode uri => uri.Uri.AbsoluteUri,
        ILiteralNode literal => literal.Value,
        _ => node.ToString() ?? "",
    };
}
