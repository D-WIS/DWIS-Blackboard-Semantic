// The semantic service host (specification 3.2, 3.4, section 8). Milestone 1: the HTTP routes
// /api/v1/manifests (POST, GET), /api/v1/sparql, /api/v1/vocabulary and /api/v1/health, internal to the
// deployment (R-EP-2). gRPC, the query registration routes and the metrics come with milestones 2 and 3.

using DWIS.Blackboard.Configuration;
using DWIS.Semantic.Host;
using DWIS.Semantic.Host.Endpoints;
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
builder.Services.AddSingleton(builder.Configuration.GetSection(SemanticOptions.StoreSection).Get<FusekiOptions>() ?? new FusekiOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ITripleStore, FusekiStore>(http => http.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IngestPipeline>();
// The registered queries (specification 3.5): evaluated against the live dataset under the query limits, one
// round after every change to the live description; the sinks of the diffs arrive with M3.4.
builder.Services.AddSingleton(sp => new StoreQueryEvaluator(sp.GetRequiredService<ITripleStore>(), sp.GetRequiredService<IOptions<QueryLimits>>().Value.ToQueryOptions(), sp.GetService<ILogger<StoreQueryEvaluator>>()));
builder.Services.AddSingleton(sp => new QueryManager(sp.GetRequiredService<StoreQueryEvaluator>().AsDelegate(), null, sp.GetService<ILogger<QueryManager>>()));
builder.Services.AddSingleton<ILiveDescriptionObserver, QueryRoundObserver>();
builder.Services.AddSingleton<VocabularyState>();
builder.Services.AddSingleton<VocabularyLoader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VocabularyLoader>());
builder.Services.AddSemanticHealthChecks();
builder.Services.AddProblemDetails();

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

var api = app.MapGroup("/api/v1");
api.MapManifests();
api.MapSparql();
api.MapService();

app.Run();

/// <summary>Lets the test project host the application with <c>WebApplicationFactory</c>.</summary>
public partial class Program
{
}
