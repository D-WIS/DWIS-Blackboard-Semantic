using System.Net;
using System.Text;
using System.Text.Json;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.Store;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// <c>PUT /api/v1/vocabulary</c> (Appendix C.7, re-derive; T-SEM-8): a version whose vocabulary graph carries
/// one more shape changes the report of the live manifest without a rebuild, and putting the previous
/// version back restores it.
/// </summary>
[Collection(SharedHost.Name)]
public sealed class VocabularyEndpointTests
{
    private static readonly ManifestId SmartConnect = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
    private readonly HostFixture _host;

    public VocabularyEndpointTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task A_version_whose_graphs_are_missing_is_404_and_a_bad_body_is_400()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var missing = await PutVersionAsync(client, "nobody-loaded-this");
        using var bad = await client.PutAsync(new Uri("/api/v1/vocabulary", UriKind.Relative), new StringContent("""{ "version": "" }""", Encoding.UTF8, "application/json"));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VersionInForceAsync(client)).Should().Be(_host.Version, "a failed switch leaves the version in force");
    }

    [Fact]
    public async Task Replacing_the_vocabulary_re_derives_the_live_manifests_and_can_be_rolled_back()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        var shaped = _host.Version + "-shaped";
        try
        {
            using (var content = new StringContent(await File.ReadAllTextAsync(HostFixture.ManifestPath("smartconnect.manifest.json")), Encoding.UTF8, "application/json"))
            using (var posted = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), content))
            {
                posted.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // The same vocabulary plus the project shape on the inferred property HasValue, as a new version in the store.
            var (vocabulary, units) = HostFixture.LoadVocabularyFiles(shaped, Path.Combine(AppContext.BaseDirectory, "Fixtures", "shapes", "after-reasoning.ttl"));
            await _host.Store.PutGraphAsync(Dataset.Live, VocabularyOptions.GraphOf(shaped), vocabulary);
            await _host.Store.PutGraphAsync(Dataset.Live, VocabularyOptions.UnitsGraphOf(shaped), units);

            using var change = await PutVersionAsync(client, shaped);
            var body = await change.Content.ReadAsStringAsync();
            change.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using (var json = JsonDocument.Parse(body))
            {
                var root = json.RootElement;
                root.GetProperty("version").GetString().Should().Be(shaped);
                root.GetProperty("previousVersion").GetString().Should().Be(_host.Version);
                var entry = root.GetProperty("manifests").EnumerateArray().Should().ContainSingle(e => e.GetProperty("manifestId").GetString() == SmartConnect.Value).Subject;
                entry.GetProperty("acceptedBefore").GetBoolean().Should().BeTrue();
                entry.GetProperty("accepted").GetBoolean().Should().BeFalse("the added shape fires on the inferred HasValue triples (T-SEM-8)");
                entry.GetProperty("errors").GetInt32().Should().Be(15);
                root.GetProperty("becameCoreOnly").EnumerateArray().Select(e => e.GetString()).Should().Equal(SmartConnect.Value);
            }

            (await VersionInForceAsync(client)).Should().Be(shaped);
            (await AskAsync(client, $"ASK {{ GRAPH <{_host.VocabularyGraph}> {{ ?s ?p ?o }} }}")).Should().BeFalse("the previous version's graphs are dropped");
            (await AcceptedAsync(client)).Should().BeFalse();

            // Rolling back: load the previous version again and put it back.
            var (previousVocabulary, previousUnits) = HostFixture.LoadVocabularyFiles(_host.Version);
            await _host.Store.PutGraphAsync(Dataset.Live, _host.VocabularyGraph, previousVocabulary);
            await _host.Store.PutGraphAsync(Dataset.Live, _host.UnitsGraph, previousUnits);
            using var back = await PutVersionAsync(client, _host.Version);
            var backBody = await back.Content.ReadAsStringAsync();
            back.StatusCode.Should().Be(HttpStatusCode.OK, backBody);
            using (var json = JsonDocument.Parse(backBody))
            {
                json.RootElement.GetProperty("becameCoreOnly").GetArrayLength().Should().Be(0);
                json.RootElement.GetProperty("manifests").EnumerateArray().Single().GetProperty("accepted").GetBoolean().Should().BeTrue();
            }

            (await AcceptedAsync(client)).Should().BeTrue();
            (await AskAsync(client, $"ASK {{ GRAPH <{VocabularyOptions.GraphOf(shaped)}> {{ ?s ?p ?o }} }}")).Should().BeFalse();
        }
        finally
        {
            using var removed = await client.DeleteAsync(new Uri($"/api/v1/manifests/{SmartConnect.Value}", UriKind.Relative));
            if (await VersionInForceAsync(client) != _host.Version)
            {
                var (previousVocabulary, previousUnits) = HostFixture.LoadVocabularyFiles(_host.Version);
                await _host.Store.PutGraphAsync(Dataset.Live, _host.VocabularyGraph, previousVocabulary);
                await _host.Store.PutGraphAsync(Dataset.Live, _host.UnitsGraph, previousUnits);
                using var restore = await PutVersionAsync(client, _host.Version);
            }

            await _host.Store.DeleteGraphAsync(Dataset.Live, VocabularyOptions.GraphOf(shaped));
            await _host.Store.DeleteGraphAsync(Dataset.Live, VocabularyOptions.UnitsGraphOf(shaped));
        }
    }

    private static Task<HttpResponseMessage> PutVersionAsync(HttpClient client, string version) =>
        client.PutAsync(new Uri("/api/v1/vocabulary", UriKind.Relative), new StringContent(JsonSerializer.Serialize(new { version }), Encoding.UTF8, "application/json"));

    private static async Task<string?> VersionInForceAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/vocabulary", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("version").GetString();
    }

    private static async Task<bool> AcceptedAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/manifests", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetProperty("value").GetString() == SmartConnect.Value).GetProperty("accepted").GetBoolean();
    }

    private static async Task<bool> AskAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("boolean").GetBoolean();
    }
}
