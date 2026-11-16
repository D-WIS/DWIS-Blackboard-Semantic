using System.Net;
using System.Text;
using System.Text.Json;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// <c>POST</c> and <c>GET /api/v1/manifests</c> with the SmartConnect manifest and its kilogram copy, and
/// the milestone 1 "done when" of roadmap F.3 through <c>GET /api/v1/sparql</c>: the two queries, one with
/// each prefix, return the same signal (T-SEM-1, T-SEM-3, T-SEM-4, T-SEM-9, T-SEM-10).
/// </summary>
[Collection(SharedHost.Name)]
public sealed class ManifestEndpointTests
{
    private const string SppByClass = """
        PREFIX dwis: <http://d-wis.org/>
        SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }
        """;

    private const string SppByQuantityAndPlace = """
        PREFIX ddhub: <http://ddhub.no/>
        SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal .
                               ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }
        """;

    private const string LegacyIriLeft = """
        ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), "http://ddhub.no/") || STRSTARTS(STR(?p), "http://ddhub.no/") || (isIRI(?o) && STRSTARTS(STR(?o), "http://ddhub.no/"))) }
        """;

    private static readonly ManifestId SmartConnect = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
    private readonly HostFixture _host;

    public ManifestEndpointTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task Inject_list_query_reject_and_restore()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        // 1. The SmartConnect manifest goes live whole (accepted = true, every warning explained by the policy).
        var (status, report) = await PostAsync(client, "smartconnect.manifest.json");
        status.Should().Be(HttpStatusCode.OK);
        report.ManifestId.Should().Be(SmartConnect.Value);
        report.Accepted.Should().BeTrue();
        report.Counts.Errors.Should().Be(0);
        report.Counts.Warnings.Should().Be(254 + 303 + 22 + 4);
        report.Live.Should().Be(new LiveParts(true, true));
        report.Statistics.Asserted.Should().BeGreaterThan(3000);
        report.Statistics.CommitMs.Should().BeGreaterThan(0);

        // 2. It is listed from its provenance and its report is stored.
        var list = await ListAsync(client);
        var entry = list.Should().ContainSingle(e => e.GetProperty("id").GetProperty("value").GetString() == SmartConnect.Value).Subject;
        entry.GetProperty("accepted").GetBoolean().Should().BeTrue();
        entry.GetProperty("manifestName").GetString().Should().Be("SmartConnect rig signals");
        entry.GetProperty("contentHash").GetString().Should().StartWith("sha256:");
        entry.GetProperty("vocabularyVersion").GetString().Should().Be(_host.Version);
        entry.GetProperty("assertedTriples").GetInt32().Should().Be(report.Statistics.Asserted);
        entry.GetProperty("inferredTriples").GetInt32().Should().Be(report.Statistics.Inferred);
        entry.GetProperty("quarantinedTriples").GetInt32().Should().Be(0);

        // 2b. The inferred graph (C.6 step 2): non-empty, disjoint from the asserted graph (T-SEM-5), separated by GRAPH (T-SEM-6).
        report.Statistics.Inferred.Should().BeGreaterThan(0);
        (await AskAsync(client, $"ASK {{ GRAPH <{SmartConnect.InferredGraph}> {{ ?s ?p ?o }} }}")).Should().BeTrue();
        (await AskAsync(client, $"ASK {{ GRAPH <{SmartConnect.InferredGraph}> {{ ?s ?p ?o }} GRAPH <{SmartConnect.AssertedGraph}> {{ ?s ?p ?o }} }}")).Should().BeFalse("no triple is both asserted and inferred");
        const string sppDataPoint = "<http://d-wis.org/SmartConnect/mainwellCirculationCommon_pressureStandPipe#01>";
        (await AskAsync(client, $"PREFIX dwis: <http://d-wis.org/> ASK {{ GRAPH <{SmartConnect.InferredGraph}> {{ {sppDataPoint} a dwis:PrototypeData }} }}")).Should().BeTrue("SPP is a subclass of PrototypeData in the vocabulary");
        (await AskAsync(client, $"PREFIX dwis: <http://d-wis.org/> ASK {{ GRAPH <{SmartConnect.AssertedGraph}> {{ {sppDataPoint} a dwis:PrototypeData }} }}")).Should().BeFalse();
        (await AskAsync(client, $"PREFIX dwis: <http://d-wis.org/> ASK {{ {sppDataPoint} a dwis:PrototypeData }}")).Should().BeTrue("the union default graph sees the inferred graph");

        using var stored = await client.GetAsync(new Uri($"/api/v1/manifests/{SmartConnect.Value}", UriKind.Relative));
        stored.StatusCode.Should().Be(HttpStatusCode.OK);
        ReportJson.Parse(await stored.Content.ReadAsStringAsync()).Should().BeEquivalentTo(report);

        using var missing = await client.GetAsync(new Uri("/api/v1/manifests/Nobody/nothing", UriKind.Relative));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 3. The two queries of roadmap F.3, one per prefix, return the same signal (T-SEM-3); no legacy IRI is stored (T-SEM-4).
        var byClass = await SelectAsync(client, SppByClass);
        var byQuantity = await SelectAsync(client, SppByQuantityAndPlace);
        byClass.Should().ContainSingle().Which.Should().Be("http://d-wis.org/SmartConnect/Variables/SmartConnect.mainwellCirculationCommon_pressureStandPipe");
        byQuantity.Should().Equal(byClass);
        (await AskAsync(client, LegacyIriLeft)).Should().BeFalse();

        // 4. Injecting the same manifest again returns the stored report and writes nothing.
        var again = await PostAsync(client, "smartconnect.manifest.json");
        again.Status.Should().Be(HttpStatusCode.OK);
        again.Report.Should().BeEquivalentTo(report, "the content hash matches the live one");

        // 4b. A second manifest that shares the subjects gets its own inferred graph (R-SEM-3): the first one's
        // inferred graph is not asserted, so it is not subtracted.
        var kilogram = ManifestId.Of(ManifestJson.Parse(await File.ReadAllTextAsync(HostFixture.ManifestPath("smartconnect-kilogram.manifest.json"))));
        var (sharedStatus, shared) = await PostAsync(client, "smartconnect-kilogram.manifest.json");
        sharedStatus.Should().Be(HttpStatusCode.OK);
        shared.Statistics.Inferred.Should().BeGreaterThan(0, "what another manifest inferred is that manifest's, not this one's");
        await RemoveAsync(client, kilogram);

        // 5. DELETE removes the four graphs (T-SEM-12, the semantic side); a second DELETE is a 404.
        await RemoveAsync(client, SmartConnect);
        (await ListAsync(client)).Should().NotContain(e => e.GetProperty("id").GetProperty("value").GetString() == SmartConnect.Value);
        (await SelectAsync(client, SppByClass)).Should().BeEmpty();
        foreach (var graph in new[] { SmartConnect.AssertedGraph, SmartConnect.InferredGraph, SmartConnect.ProvenanceGraph, SmartConnect.StagingGraph })
        {
            (await AskAsync(client, $"ASK {{ GRAPH <{graph}> {{ ?s ?p ?o }} }}")).Should().BeFalse(graph);
        }

        using var again404 = await client.DeleteAsync(new Uri($"/api/v1/manifests/{SmartConnect.Value}", UriKind.Relative));
        again404.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 6. The kilogram copy (one wrong unit) goes live core-only: one error, the whole manifest in quarantine (T-SEM-9).
        var (kiloStatus, kilo) = await PostAsync(client, "smartconnect-kilogram.manifest.json");
        kiloStatus.Should().Be(HttpStatusCode.OK);
        kilo.ManifestId.Should().Be(kilogram.Value);
        kilo.Accepted.Should().BeFalse();
        kilo.Counts.Errors.Should().Be(1);
        kilo.Findings[0].Code.Should().Be("unit-quantity-dimension");
        kilo.Live.Should().Be(new LiveParts(true, false));
        kilo.Statistics.Asserted.Should().BeLessThan(report.Statistics.Asserted);

        var kiloEntry = (await ListAsync(client)).Single(e => e.GetProperty("id").GetProperty("value").GetString() == kilogram.Value);
        kiloEntry.GetProperty("accepted").GetBoolean().Should().BeFalse();
        kiloEntry.GetProperty("quarantinedTriples").GetInt32().Should().BeGreaterThan(kilo.Statistics.Asserted);
        kilo.Statistics.Inferred.Should().BeGreaterThan(0, "the core is reasoned over too");
        kilo.Statistics.Inferred.Should().BeLessThan(report.Statistics.Inferred, "less is asserted, so less is inferred");

        (await SelectAsync(client, SppByClass)).Should().Equal(byClass, "the data point and its signal are core");
        (await SelectAsync(client, SppByQuantityAndPlace)).Should().BeEmpty("the quantity and the location are not core");
        (await _host.Store.GetGraphAsync(Dataset.Quarantine, kilogram.QuarantineGraph)).Should().NotBeNull("the whole manifest is in quarantine");
        (await AskAsync(client, $"ASK {{ GRAPH <{kilogram.QuarantineGraph}> {{ ?s ?p ?o }} }}")).Should().BeFalse("the quarantine dataset is invisible to live queries (T-SEM-10)");

        // 7. The good manifest comes back whole, and an accepted commit clears a stale quarantine graph of its own id.
        (await AskAsync(client, $"ASK {{ GRAPH <{kilogram.InferredGraph}> {{ ?s ?p ?o }} }}")).Should().BeTrue();
        await RemoveAsync(client, kilogram);
        (await _host.Store.GetGraphAsync(Dataset.Quarantine, kilogram.QuarantineGraph)).Should().BeNull("DELETE drops the quarantine graph too");
        await _host.Store.PutGraphAsync(Dataset.Quarantine, SmartConnect.QuarantineGraph, kilo.Accepted ? new VDS.RDF.Graph() : StaleGraph());
        var (restoredStatus, restored) = await PostAsync(client, "smartconnect.manifest.json");
        restoredStatus.Should().Be(HttpStatusCode.OK);
        restored.Accepted.Should().BeTrue();
        (await SelectAsync(client, SppByQuantityAndPlace)).Should().Equal(byClass);
        (await _host.Store.GetGraphAsync(Dataset.Quarantine, SmartConnect.QuarantineGraph)).Should().BeNull();
    }

    private static async Task RemoveAsync(HttpClient client, ManifestId id)
    {
        using var response = await client.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Update_replaces_the_graphs_without_a_reader_ever_seeing_a_mix()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        const string dataPoint = "<http://d-wis.org/SmartConnect/mainwellCirculationCommon_pressureStandPipe#01>";
        const string where = "PREFIX dwis: <http://d-wis.org/> SELECT ?e WHERE { " + dataPoint + " dwis:IsLocatedAtEquipment ?e }";
        var original = ManifestJson.Parse(await File.ReadAllTextAsync(HostFixture.ManifestPath("smartconnect.manifest.json")));
        // The same manifest with the standpipe pressure moved to a second standpipe node: same id, another graph.
        var moved = original with
        {
            InjectedNodes = [.. original.InjectedNodes, new InjectedNode { TypeDictionaryURI = "MudStandpipe", BrowseName = "standpipe2", UniqueName = "standpipe2", DisplayName = "standpipe2" }],
            InjectedReferences = original.InjectedReferences.Select(r =>
                r.Subject.ID == "mainwellCirculationCommon_pressureStandPipe#01" && r.VerbURI.EndsWith("IsLocatedAtEquipment", StringComparison.Ordinal)
                    ? r with { Object = r.Object with { ID = "standpipe2" } }
                    : r).ToList(),
        };
        ManifestId.Of(moved).Should().Be(SmartConnect);
        try
        {
            (await PostAsync(client, "smartconnect.manifest.json")).Report.Accepted.Should().BeTrue();
            (await SelectAsync(client, where)).Should().Equal("http://d-wis.org/SmartConnect/standpipe");

            // A reader keeps asking where the data point is while the update runs.
            using var stop = new CancellationTokenSource();
            var seen = new List<string>();
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    seen.Add(string.Join("|", await SelectAsync(client, where)));
                }
            });

            using var put = await client.PutAsync(new Uri($"/api/v1/manifests/{SmartConnect.Value}", UriKind.Relative),
                new StringContent(ManifestJson.Serialize(moved), Encoding.UTF8, "application/json"));
            var body = await put.Content.ReadAsStringAsync();
            put.StatusCode.Should().Be(HttpStatusCode.OK, body);
            ReportJson.Parse(body).Accepted.Should().BeTrue(body);
            stop.Cancel();
            await reader;

            seen.Should().NotBeEmpty();
            seen.Distinct().Should().BeSubsetOf(["http://d-wis.org/SmartConnect/standpipe", "http://d-wis.org/SmartConnect/standpipe2", ""],
                "a reader sees the old graph, the new graph or no manifest, never a mix (T-SEM-11)");
            (await SelectAsync(client, where)).Should().Equal("http://d-wis.org/SmartConnect/standpipe2");
            (await ListAsync(client)).Should().ContainSingle(e => e.GetProperty("id").GetProperty("value").GetString() == SmartConnect.Value);

            // PUT under another id than the manifest's own is refused.
            using var mismatch = await client.PutAsync(new Uri("/api/v1/manifests/SmartConnect/another-name", UriKind.Relative),
                new StringContent(ManifestJson.Serialize(moved), Encoding.UTF8, "application/json"));
            mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await mismatch.Content.ReadAsStringAsync()).Should().Contain("Id mismatch");
        }
        finally
        {
            using var cleanup = await client.DeleteAsync(new Uri($"/api/v1/manifests/{SmartConnect.Value}", UriKind.Relative));
        }
    }

    private static VDS.RDF.Graph StaleGraph()
    {
        var graph = new VDS.RDF.Graph();
        graph.Assert(new VDS.RDF.Triple(graph.CreateUriNode(new Uri("http://d-wis.org/test/s")), graph.CreateUriNode(new Uri("http://d-wis.org/test/p")), graph.CreateLiteralNode("stale")));
        return graph;
    }

    [Fact]
    public async Task A_shape_that_fires_only_after_reasoning_downgrades_the_manifest_to_core_only()
    {
        _host.SkipUnlessAvailable();
        // A vocabulary of its own: the same files plus a project shape on the inferred property HasValue
        // (Fixtures/shapes/after-reasoning.ttl), under a version of its own, and a host configured for it.
        var version = _host.Version + "-reasoning";
        var vocabularyGraph = "http://d-wis.org/vocabulary/" + version;
        var unitsGraph = "http://d-wis.org/units/" + version;
        var id = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
        try
        {
            var (vocabulary, units) = HostFixture.LoadVocabularyFiles(version, Path.Combine(AppContext.BaseDirectory, "Fixtures", "shapes", "after-reasoning.ttl"));
            await _host.Store.PutGraphAsync(Dataset.Live, vocabularyGraph, vocabulary);
            await _host.Store.PutGraphAsync(Dataset.Live, unitsGraph, units);
            using var factory = _host.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<VocabularyOptions>(o => o.Version = version)));
            using var client = factory.CreateClient();
            await HostFixture.WaitUntilLoadedAsync(client);

            var (status, report) = await PostAsync(client, "smartconnect.manifest.json");

            status.Should().Be(HttpStatusCode.OK);
            report.Accepted.Should().BeFalse("the shape fires on the inferred HasValue triples");
            report.Live.Should().Be(new LiveParts(true, false));
            var afterReasoning = report.Findings.Where(f => f.Stage == Stage.Commit).ToList();
            afterReasoning.Should().HaveCount(15).And.OnlyContain(f => f.Severity == Severity.Error && f.Source == "http://d-wis.org/shapes/ValueNeedsQuantityShape");
            report.Findings.Where(f => f.Stage == Stage.Preprocessing && f.Severity == Severity.Error).Should().BeEmpty("the pass alone accepted the manifest");
            report.Statistics.Asserted.Should().BeLessThan(3593, "the core replaced the whole graph");
            report.Statistics.Inferred.Should().BeGreaterThan(0, "the core is reasoned over again");
            (await _host.Store.GetGraphAsync(Dataset.Quarantine, id.QuarantineGraph)).Should().NotBeNull();
            (await AskAsync(client, $"ASK {{ GRAPH <{id.AssertedGraph}> {{ ?s <http://d-wis.org/HasUnitOfMeasure> ?o }} }}")).Should().BeFalse("units are not core");
            (await SelectAsync(client, SppByClass)).Should().ContainSingle();
        }
        finally
        {
            using var removed = await _host.CreateClient().DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
            await _host.Store.DeleteGraphAsync(Dataset.Live, vocabularyGraph);
            await _host.Store.DeleteGraphAsync(Dataset.Live, unitsGraph);
        }
    }

    [Fact]
    public async Task Bad_input_is_400_with_the_reason()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var notJson = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), new StringContent("{ not json", Encoding.UTF8, "application/json"));
        notJson.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await notJson.Content.ReadAsStringAsync()).Should().Contain("Not a manifest");

        using var noProvider = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), new StringContent("""{ "ManifestName": "x" }""", Encoding.UTF8, "application/json"));
        noProvider.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noProvider.Content.ReadAsStringAsync()).Should().Contain("provider name");
    }

    private static async Task<(HttpStatusCode Status, IngestionReport Report)> PostAsync(HttpClient client, string manifest)
    {
        using var content = new StringContent(await File.ReadAllTextAsync(HostFixture.ManifestPath(manifest)), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return (response.StatusCode, ReportJson.Parse(body));
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/manifests", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<List<string>> SelectAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var variable = json.RootElement.GetProperty("head").GetProperty("vars")[0].GetString()!;
        return json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray()
            .Select(b => b.GetProperty(variable).GetProperty("value").GetString()!).ToList();
    }

    private static async Task<bool> AskAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("boolean").GetBoolean();
    }
}
