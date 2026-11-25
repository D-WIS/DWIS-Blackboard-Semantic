using System.Text.Json;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;
using DWIS.Semantic.Store.Tests;
using DWIS.Semantic.Validation;
using Microsoft.AspNetCore.Mvc.Testing;
using VDS.RDF;

namespace DWIS.Semantic.Host.Tests;

/// <summary>The one host and the one Fuseki shared by the tests of this assembly.</summary>
[CollectionDefinition(Name)]
public sealed class SharedHost : ICollectionFixture<HostFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "host";
}

/// <summary>
/// A Fuseki (found as the store's contract tests find it) with the vocabulary of 14 October 2026, its shapes
/// and the units loaded under a version of its own, and the host started against it through the test server.
/// The store address, the version and the policy file reach the host as <c>DWIS__</c> environment variables
/// (layer 4 of specification section 8), exactly as the compose stack sets them.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    private static readonly string[] TestManifests =
    [
        "smartconnect.manifest.json",
        "smartconnect-kilogram.manifest.json",
        "small/smartconnect-01.manifest.json",
        "small/smartconnect-02.manifest.json",
        "small/smartconnect-03.manifest.json",
        // The four accepted manifests of the library (the 2023 one has no manifest name and never goes live).
        "library/norce-digital-twin/manifest.json",
        "library/openlab-rig-signals/manifest.json",
        "library/well-a-rig-data/manifest.json",
        "library/well-a-transient-model/manifest.json",
    ];
    private readonly FusekiFixture _fuseki = new();
    private WebApplicationFactory<Program>? _factory;

    /// <summary>The version the vocabulary is loaded under, unique to this run.</summary>
    public string Version { get; } = "test-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The vocabulary graph in the store.</summary>
    public string VocabularyGraph => "http://d-wis.org/vocabulary/" + Version;

    /// <summary>The units graph in the store.</summary>
    public string UnitsGraph => "http://d-wis.org/units/" + Version;

    /// <summary>The store the host uses.</summary>
    public FusekiStore Store => _fuseki.Store;

    /// <summary>The host.</summary>
    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("No host.");

    /// <summary>Where the manifests of the tests are.</summary>
    public static string ManifestPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifests", name);

    /// <summary>The policy file the host reads.</summary>
    public static string PolicyPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary", "validation-policy.json");

    /// <summary>
    /// The vocabulary graph (vocabulary, advanced definitions and shapes, plus any extra shape files) and
    /// the units graph as the compose loader writes them, from the fixture files.
    /// </summary>
    public static (Graph Vocabulary, Graph Units) LoadVocabularyFiles(string version, params string[] extraShapeFiles)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary");
        var advanced = Path.Combine(root, "advancedDefinitions");
        var model = VocabularyModel.Load(
            version,
            [
                Path.Combine(root, "DWISVocabulary.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Noun.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Prototypes.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Units.ttl"),
            ],
            [Path.Combine(advanced, "DWISVocabularyShapes.ttl"), .. extraShapeFiles],
            [Path.Combine(root, "unit-and-quantity-individuals.ttl")]);
        var vocabulary = new Graph();
        vocabulary.Merge(model.Graph);
        vocabulary.Merge(model.ShapesGraph!);
        return (vocabulary, model.ReferenceData);
    }

    /// <summary>Skips the calling test when no Fuseki is available.</summary>
    public void SkipUnlessAvailable() => _fuseki.SkipUnlessAvailable();

    /// <summary>A client of the shared host.</summary>
    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Waits until the host reports the vocabulary loaded.</summary>
    public static async Task WaitUntilLoadedAsync(HttpClient client, TimeSpan? limit = null)
    {
        using var cts = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            using var response = await client.GetAsync(new Uri("/api/v1/vocabulary", UriKind.Relative), cts.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            if (json.RootElement.GetProperty("loaded").GetBoolean())
            {
                return;
            }

            await Task.Delay(250, cts.Token);
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fuseki.InitializeAsync();
        if (_fuseki.SkipReason is not null)
        {
            return;
        }

        // The same files the validation tests use, translated to the canonical namespace on the way in.
        var (vocabulary, units) = LoadVocabularyFiles(Version);
        await Store.PutGraphAsync(Dataset.Live, VocabularyGraph, vocabulary);
        await Store.PutGraphAsync(Dataset.Live, UnitsGraph, units);

        Environment.SetEnvironmentVariable("DWIS__Store__BaseUrl", _fuseki.BaseUrl.ToString());
        Environment.SetEnvironmentVariable("DWIS__Vocabulary__Version", Version);
        Environment.SetEnvironmentVariable("DWIS__Vocabulary__RetrySeconds", "1");
        Environment.SetEnvironmentVariable("DWIS__Validation__PolicyFile", PolicyPath);
        _factory = new WebApplicationFactory<Program>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_fuseki.SkipReason is null)
        {
            foreach (var id in TestManifests.Select(f => ManifestId.Of(Blackboard.Contracts.Manifests.ManifestJson.Parse(File.ReadAllText(ManifestPath(f))))))
            {
                await Store.DeleteGraphAsync(Dataset.Live, id.AssertedGraph);
                await Store.DeleteGraphAsync(Dataset.Live, id.InferredGraph);
                await Store.DeleteGraphAsync(Dataset.Live, id.ProvenanceGraph);
                await Store.DeleteGraphAsync(Dataset.Live, id.StagingGraph);
                await Store.DeleteGraphAsync(Dataset.Quarantine, id.QuarantineGraph);
            }

            await Store.DeleteGraphAsync(Dataset.Live, VocabularyGraph);
            await Store.DeleteGraphAsync(Dataset.Live, UnitsGraph);
        }

        await _fuseki.DisposeAsync();
    }
}
