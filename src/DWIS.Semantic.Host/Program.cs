// The semantic service host (specification 3.2, 3.4, section 8). Milestone 1: the HTTP routes
// /api/v1/manifests (POST, GET), /api/v1/sparql, /api/v1/vocabulary and /api/v1/health, internal to the
// deployment (R-EP-2). gRPC, the query registration routes and the metrics come with milestones 2 and 3.

using DWIS.Blackboard.Configuration;
using DWIS.Semantic.Host;
using DWIS.Semantic.Host.Endpoints;
using DWIS.Semantic.Host.Grpc;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.QueryRegistration;
using DWIS.Semantic.Store;
using DWIS.Semantic.Validation;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The five configuration layers of section 8 (R-CFG-1) on top of the host's own sources, so that
// DWIS__Store__BaseUrl, the shared file and --Store:BaseUrl=… win over appsettings.json.
var layers = new DwisConfigurationOptions
{
    BasePath = builder.Environment.ContentRootPath,
    EnvironmentName = builder.Environment.EnvironmentName,
    CommandLineArguments = args,
};
foreach (var (key, value) in SemanticOptions.Defaults)
{
    layers.Defaults[key] = value;
}

builder.Configuration.AddDwisLayers(layers);

builder.Services.Configure<VocabularyOptions>(builder.Configuration.GetSection(SemanticOptions.VocabularySection));
builder.Services.Configure<ValidationOptions>(builder.Configuration.GetSection(SemanticOptions.ValidationSection));
builder.Services.Configure<QueryLimits>(builder.Configuration.GetSection(SemanticOptions.QuerySection));
builder.Services.Configure<StagingOptions>(builder.Configuration.GetSection(SemanticOptions.StagingSection));
builder.Services.Configure<InferenceOptions>(builder.Configuration.GetSection(SemanticOptions.InferenceSection));
builder.Services.AddSingleton(builder.Configuration.GetSection(SemanticOptions.StoreSection).Get<FusekiOptions>() ?? new FusekiOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ITripleStore, FusekiStore>(http => http.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton(sp => new StagingTable(sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<StagingOptions>>().Value.TimeLimitSeconds)));
// The reasoning rules of the commit (decisions D-64 and D-65): the embedded files, or the ones the configuration names.
builder.Services.AddSingleton(sp => { var o = sp.GetRequiredService<IOptions<InferenceOptions>>().Value; return Inference.FromFiles(o.RulesFile, o.AnswerFile); });
builder.Services.AddSingleton(sp => new IngestPipeline(sp.GetRequiredService<ITripleStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetServices<ILiveDescriptionObserver>(), sp.GetService<ILogger<IngestPipeline>>(), sp.GetRequiredService<StagingTable>(), sp.GetRequiredService<Inference>()));
// The registered queries (specification 3.5): evaluated against the live dataset under the query limits, one
// round after every change to the live description; the broadcaster fans the diffs out to the WatchQueries
// streams and the server-sent events (M3.4).
builder.Services.AddSingleton(sp => new StoreQueryEvaluator(sp.GetRequiredService<ITripleStore>(), sp.GetRequiredService<IOptions<QueryLimits>>().Value.ToQueryOptions(), sp.GetService<ILogger<StoreQueryEvaluator>>()));
builder.Services.AddSingleton(sp => new QueryManager(sp.GetRequiredService<StoreQueryEvaluator>().AsDelegate(), null, sp.GetService<ILogger<QueryManager>>()));
builder.Services.AddSingleton<QueryDiffBroadcaster>();
builder.Services.AddSingleton<IQueryDiffSink>(sp => sp.GetRequiredService<QueryDiffBroadcaster>());
builder.Services.AddSingleton<ILiveDescriptionObserver, QueryRoundObserver>();
builder.Services.AddSingleton<VocabularyState>();
builder.Services.AddSingleton<VocabularyLoader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VocabularyLoader>());
builder.Services.AddSemanticHealthChecks();
builder.Services.AddProblemDetails();
// The gRPC service of specification 3.3 (D-10), on the same port as the HTTP API: HTTP/2 without TLS inside the
// Docker network (prior knowledge, which Grpc.Net.Client uses for http:// addresses); TLS comes with M5.
builder.Services.AddGrpc();

var app = builder.Build();

// The effective configuration with the source of each value (R-CFG-2), the service's own sections only.
var effective = EffectiveConfiguration.Describe((IConfigurationRoot)app.Configuration)
    .Where(e => SemanticOptions.Sections.Any(s => e.Key.StartsWith(s + ":", StringComparison.OrdinalIgnoreCase)))
    .ToList();
Log.EffectiveConfiguration(app.Logger, effective.Count, EffectiveConfiguration.Mask,
    string.Join(Environment.NewLine, effective.Select(e => $"  {e.Key} = {e.Value ?? "(null)"}  [{e.Source}]")));

// The validation policy (decisions D-45, D-46): a missing file is a configuration error, not a silent "no policy".
var validation = app.Configuration.GetSection(SemanticOptions.ValidationSection).Get<ValidationOptions>() ?? new ValidationOptions();
var state = app.Services.GetRequiredService<VocabularyState>();
if (!string.IsNullOrWhiteSpace(validation.PolicyFile))
{
    state.Policy = ValidationPolicy.Load(validation.PolicyFile);
    state.PolicyFile = validation.PolicyFile;
    Log.ValidationPolicy(app.Logger, validation.PolicyFile, state.Policy.Rules.Count);
}
else
{
    Log.NoValidationPolicy(app.Logger);
}

// The reasoning rules (decision D-65): which ones run is read from the file, so say it once at start-up.
var inferenceOptions = app.Configuration.GetSection(SemanticOptions.InferenceSection).Get<InferenceOptions>() ?? new InferenceOptions();
var inferenceRules = app.Services.GetRequiredService<Inference>().Rules;
Log.InferenceRules(app.Logger, string.IsNullOrWhiteSpace(inferenceOptions.RulesFile) ? "the embedded inference-rules.json" : inferenceOptions.RulesFile,
    inferenceRules.Enabled.Count(), string.Join(", ", inferenceRules.Enabled.Select(r => r.Name)),
    inferenceRules.Disabled.Count(), string.Join(", ", inferenceRules.Disabled.Select(r => r.Name)));

var api = app.MapGroup("/api/v1");
api.MapManifests();
api.MapSparql();
api.MapService();
api.MapQueries();
app.MapGrpcService<SemanticGrpcService>();

app.Run();

/// <summary>Lets the test project host the application with <c>WebApplicationFactory</c>.</summary>
public partial class Program
{
}
