using System.Net;
using System.Text;
using System.Text.Json;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// The calls the host tests make through the HTTP API, and the queries of roadmap F.3 (milestone 1 "done when"):
/// the standpipe pressure by its class, and by its quantity and place, with either prefix.
/// </summary>
internal static class HostClient
{
    /// <summary>The SPP signal by the class of its data point (canonical prefix).</summary>
    internal const string SppByClass = """
        PREFIX dwis: <http://d-wis.org/>
        SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }
        """;

    /// <summary>The SPP signal by the quantity of its data point and the equipment it is at (legacy prefix).</summary>
    internal const string SppByQuantityAndPlace = """
        PREFIX ddhub: <http://ddhub.no/>
        SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal .
                               ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }
        """;

    /// <summary>True when any legacy IRI is stored (T-SEM-4 fails).</summary>
    internal const string LegacyIriLeft = """
        ASK { ?s ?p ?o . FILTER (STRSTARTS(STR(?s), "http://ddhub.no/") || STRSTARTS(STR(?p), "http://ddhub.no/") || (isIRI(?o) && STRSTARTS(STR(?o), "http://ddhub.no/"))) }
        """;

    /// <summary>Injects a fixture manifest (by its name under <c>Fixtures/manifests</c>) and returns the report; the call must succeed.</summary>
    internal static async Task<(HttpStatusCode Status, IngestionReport Report)> PostAsync(HttpClient client, string manifest)
    {
        using var content = new StringContent(await File.ReadAllTextAsync(HostFixture.ManifestPath(manifest)), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return (response.StatusCode, ReportJson.Parse(body));
    }

    /// <summary>Removes a live manifest; the call must succeed.</summary>
    internal static async Task RemoveAsync(HttpClient client, ManifestId id)
    {
        using var response = await client.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>The entries of <c>GET /manifests</c>.</summary>
    internal static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/manifests", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>The values of the first variable of a <c>SELECT</c> through <c>GET /sparql</c>.</summary>
    internal static async Task<List<string>> SelectAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var variable = json.RootElement.GetProperty("head").GetProperty("vars")[0].GetString()!;
        return json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray()
            .Select(b => b.GetProperty(variable).GetProperty("value").GetString()!).ToList();
    }

    /// <summary>The answer of an <c>ASK</c> through <c>GET /sparql</c>.</summary>
    internal static async Task<bool> AskAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("boolean").GetBoolean();
    }
}
