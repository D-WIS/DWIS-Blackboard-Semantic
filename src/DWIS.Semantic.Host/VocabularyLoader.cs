using DWIS.Semantic.Store;
using DWIS.Semantic.Validation;
using Microsoft.Extensions.Options;
using VDS.RDF;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;

namespace DWIS.Semantic.Host;

/// <summary>What the service knows about its vocabulary: which version, loaded or not, and why not.</summary>
public sealed class VocabularyState
{
    private readonly object _gate = new();
    private VocabularyModel? _model;
    private string _version;

    /// <summary>Starts on the configured version.</summary>
    public VocabularyState(Microsoft.Extensions.Options.IOptions<VocabularyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _version = options.Value.Version;
    }

    /// <summary>The version in force: the graphs <c>http://d-wis.org/vocabulary/{version}</c> and <c>http://d-wis.org/units/{version}</c>.</summary>
    public string Version
    {
        get { lock (_gate) { return _version; } }
        internal set { lock (_gate) { _version = value; } }
    }

    /// <summary>The vocabulary graph IRI of the version in force.</summary>
    public string Graph => VocabularyOptions.GraphOf(Version);

    /// <summary>The units graph IRI of the version in force.</summary>
    public string UnitsGraph => VocabularyOptions.UnitsGraphOf(Version);

    /// <summary>The model, or null until the vocabulary is read from the store.</summary>
    public VocabularyModel? Model
    {
        get { lock (_gate) { return _model; } }
    }

    /// <summary>The policy in force (<see cref="ValidationPolicy.None"/> when no file is configured).</summary>
    public ValidationPolicy Policy { get; internal set; } = ValidationPolicy.None;

    /// <summary>The policy file in force, or empty.</summary>
    public string PolicyFile { get; internal set; } = "";

    /// <summary>When the vocabulary was read, or null.</summary>
    public DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>Triples in the vocabulary graph.</summary>
    public int Triples { get; private set; }

    /// <summary>Triples in the units graph.</summary>
    public int UnitTriples { get; private set; }

    /// <summary>Why the last attempt failed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>True once a model is there.</summary>
    public bool Loaded => Model is not null;

    internal void Set(VocabularyModel model, int triples, int unitTriples, DateTimeOffset at)
    {
        lock (_gate)
        {
            _model = model;
            Triples = triples;
            UnitTriples = unitTriples;
            LoadedAt = at;
            Error = null;
        }
    }

    internal void Fail(string error)
    {
        lock (_gate)
        {
            Error = error;
        }
    }
}

/// <summary>
/// Reads the vocabulary graph and the units graph from the store at start-up and builds the model the
/// pre-processing pass needs. The graphs are put there by the loader of the compose stack
/// (DWIS-Blackboard-Tests, <c>compose/load-vocabulary.sh</c>); until they are there the service keeps
/// trying every <see cref="VocabularyOptions.RetrySeconds"/> and reports itself unhealthy. The SHACL shapes
/// live in the vocabulary graph (the loader puts the advanced definitions there), so the same graph serves
/// as the shapes graph.
/// </summary>
public sealed class VocabularyLoader : BackgroundService
{
    private readonly ITripleStore _store;
    private readonly VocabularyState _state;
    private readonly VocabularyOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VocabularyLoader> _logger;

    /// <summary>Creates the loader.</summary>
    public VocabularyLoader(ITripleStore store, VocabularyState state, IOptions<VocabularyOptions> options, TimeProvider clock, ILogger<VocabularyLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _state = state;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>One attempt at the version in force: true when the model is now loaded.</summary>
    public Task<bool> TryLoadAsync(CancellationToken cancellationToken) => TryLoadAsync(_state.Version, cancellationToken);

    /// <summary>
    /// Switches to another version (<c>PUT /vocabulary</c>): the model is built from that version's graphs
    /// first, and only then does the state change, so a failed switch leaves the service on the version it
    /// had. True when the switch happened.
    /// </summary>
    public async Task<bool> SwitchAsync(string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return await TryLoadAsync(version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryLoadAsync(string version, CancellationToken cancellationToken)
    {
        var graph = VocabularyOptions.GraphOf(version);
        var unitsGraph = VocabularyOptions.UnitsGraphOf(version);
        try
        {
            var vocabulary = await _store.GetGraphAsync(Dataset.Live, graph, cancellationToken).ConfigureAwait(false);
            if (vocabulary is null || vocabulary.IsEmpty)
            {
                _state.Fail($"The vocabulary graph <{graph}> is not in the store yet.");
                return false;
            }

            var units = await _store.GetGraphAsync(Dataset.Live, unitsGraph, cancellationToken).ConfigureAwait(false);
            if (units is null || units.IsEmpty)
            {
                _state.Fail($"The units graph <{unitsGraph}> is not in the store yet.");
                return false;
            }

            // Decision D-64: the inference rules take the schema as closed (one step of subclass or subproperty
            // is the whole chain), so a version loaded without its closure would under-derive silently.
            if (!IsClosed(vocabulary) || !IsClosed(units))
            {
                _state.Fail($"The vocabulary graph <{graph}> or the units graph <{unitsGraph}> is not closed (no <{ClosedBy}> marker): load the closure of the version with it (decision D-47; the compose loader with VOCABULARY_CLOSURE=on).");
                return false;
            }

            var model = VocabularyModel.FromGraphs(version, AsGraph(vocabulary), AsGraph(vocabulary), AsGraph(units));
            _state.Version = version;
            _state.Set(model, vocabulary.Triples.Count, units.Triples.Count, _clock.GetUtcNow());
            Log.VocabularyLoaded(_logger, version, vocabulary.Triples.Count, units.Triples.Count, model.Classes.Count, model.Shapes is null ? "none" : "loaded");
            return true;
        }
        catch (Exception e) when (e is StoreException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _state.Fail($"{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.RetrySeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryLoadAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            Log.VocabularyRetry(_logger, delay, _state.Error);
            try
            {
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The marker the closure tool writes in each closed graph (<c>tools/closure/make-closure.py</c> of the Tests repository).</summary>
    public const string ClosedBy = "http://d-wis.org/blackboard/closedBy";

    private static bool IsClosed(IGraph graph) =>
        graph.Triples.Any(t => t.Predicate is IUriNode p && p.Uri.AbsoluteUri == ClosedBy);

    private static Graph AsGraph(IGraph graph)
    {
        if (graph is Graph concrete)
        {
            return concrete;
        }

        var copy = new Graph();
        copy.Merge(graph);
        return copy;
    }
}
