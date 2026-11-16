using System.Net;
using System.Text;
using System.Text.Json;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;

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
        entry.GetProperty("quarantinedTriples").GetInt32().Should().Be(0);

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

        // 5. Removing a manifest is a milestone 3 route; until then the store does it (C.7: drop its graphs).
        await RemoveAsync(SmartConnect);
        (await ListAsync(client)).Should().NotContain(e => e.GetProperty("id").GetProperty("value").GetString() == SmartConnect.Value);
        (await SelectAsync(client, SppByClass)).Should().BeEmpty();

        // 6. The kilogram copy (one wrong unit) goes live core-only: one error, the whole manifest in quarantine (T-SEM-9).
        var kilogram = ManifestId.Of(ManifestJson.Parse(await File.ReadAllTextAsync(HostFixture.ManifestPath("smartconnect-kilogram.manifest.json"))));
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

        (await SelectAsync(client, SppByClass)).Should().Equal(byClass, "the data point and its signal are core");
        (await SelectAsync(client, SppByQuantityAndPlace)).Should().BeEmpty("the quantity and the location are not core");
        (await _host.Store.GetGraphAsync(Dataset.Quarantine, kilogram.QuarantineGraph)).Should().NotBeNull("the whole manifest is in quarantine");
        (await AskAsync(client, $"ASK {{ GRAPH <{kilogram.QuarantineGraph}> {{ ?s ?p ?o }} }}")).Should().BeFalse("the quarantine dataset is invisible to live queries (T-SEM-10)");

        // 7. The good manifest comes back whole, and an accepted commit clears a stale quarantine graph of its own id.
        await RemoveAsync(kilogram);
        await _host.Store.PutGraphAsync(Dataset.Quarantine, SmartConnect.QuarantineGraph, kilo.Accepted ? new VDS.RDF.Graph() : StaleGraph());
        var (restoredStatus, restored) = await PostAsync(client, "smartconnect.manifest.json");
        restoredStatus.Should().Be(HttpStatusCode.OK);
        restored.Accepted.Should().BeTrue();
        (await SelectAsync(client, SppByQuantityAndPlace)).Should().Equal(byClass);
        (await _host.Store.GetGraphAsync(Dataset.Quarantine, SmartConnect.QuarantineGraph)).Should().BeNull();
    }

    private async Task RemoveAsync(ManifestId id)
    {
        await _host.Store.DeleteGraphAsync(Dataset.Live, id.AssertedGraph);
        await _host.Store.DeleteGraphAsync(Dataset.Live, id.ProvenanceGraph);
    }

    private static VDS.RDF.Graph StaleGraph()
    {
        var graph = new VDS.RDF.Graph();
        graph.Assert(new VDS.RDF.Triple(graph.CreateUriNode(new Uri("http://d-wis.org/test/s")), graph.CreateUriNode(new Uri("http://d-wis.org/test/p")), graph.CreateLiteralNode("stale")));
        return graph;
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
