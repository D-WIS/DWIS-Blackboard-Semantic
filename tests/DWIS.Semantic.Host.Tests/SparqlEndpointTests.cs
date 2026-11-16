using System.Net;
using System.Text;
using DWIS.Semantic.Host.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DWIS.Semantic.Host.Tests;

/// <summary><c>/api/v1/sparql</c>: protocol forms, answer formats, the limits (T-SEM-2) and errors.</summary>
[Collection(SharedHost.Name)]
public sealed class SparqlEndpointTests
{
    private readonly HostFixture _host;

    public SparqlEndpointTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public void Legacy_IRIs_in_a_query_are_read_as_canonical()
    {
        SparqlEndpoints.Canonical("PREFIX ddhub: <http://ddhub.no/> SELECT * WHERE { ?s a <http://ddhub.no/SPP> }")
            .Should().Be("PREFIX ddhub: <http://d-wis.org/> SELECT * WHERE { ?s a <http://d-wis.org/SPP> }");
        SparqlEndpoints.Canonical("SELECT * WHERE { ?s ?p <http://example.org/ddhub.no/x> }").Should().Contain("http://example.org/ddhub.no/x");
        SparqlEndpoints.Canonical("""ASK { ?s ?p ?o FILTER (STRSTARTS(STR(?s), "http://ddhub.no/")) }""").Should().Contain("\"http://ddhub.no/\"", "string literals are not IRIs");
    }

    [Fact]
    public async Task Get_post_body_and_post_form_give_the_same_answer()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        const string query = "PREFIX dwis: <http://d-wis.org/> ASK { dwis:SPP ?p ?o }";

        using var get = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        using var body = await client.PostAsync(new Uri("/api/v1/sparql", UriKind.Relative), new StringContent(query, Encoding.UTF8, "application/sparql-query"));
        using var form = await client.PostAsync(new Uri("/api/v1/sparql", UriKind.Relative), new FormUrlEncodedContent([new KeyValuePair<string, string>("query", query)]));

        foreach (var response in new[] { get, body, form })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/sparql-results+json");
            (await response.Content.ReadAsStringAsync()).Should().Contain("\"boolean\": true").And.Contain("true");
        }
    }

    [Fact]
    public async Task Tables_come_as_json_csv_or_xml_and_graphs_as_turtle_or_ntriples()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        const string select = "PREFIX dwis: <http://d-wis.org/> SELECT ?c WHERE { dwis:SPP <http://www.w3.org/2000/01/rdf-schema#subClassOf> ?c } LIMIT 1";
        const string construct = "PREFIX dwis: <http://d-wis.org/> CONSTRUCT { dwis:SPP ?p ?o } WHERE { dwis:SPP ?p ?o }";

        (await GetAsync(client, select, "text/csv")).MediaType.Should().Be("text/csv");
        (await GetAsync(client, select, "application/sparql-results+xml")).MediaType.Should().Be("application/sparql-results+xml");
        (await GetAsync(client, select, null)).MediaType.Should().Be("application/sparql-results+json");
        var turtle = await GetAsync(client, construct, null);
        turtle.MediaType.Should().Be("text/turtle");
        turtle.Body.Should().Contain("SPP");
        (await GetAsync(client, construct, "application/n-triples")).MediaType.Should().Be("application/n-triples");
    }

    [Fact]
    public async Task The_row_limit_truncates_and_says_so()
    {
        _host.SkipUnlessAvailable();
        using var factory = _host.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<QueryLimits>(q => q.RowLimit = 3)));
        using var client = factory.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var cut = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString("SELECT ?s WHERE { ?s ?p ?o } LIMIT 10"), UriKind.Relative));
        using var whole = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString("SELECT ?s WHERE { ?s ?p ?o } LIMIT 3"), UriKind.Relative));

        cut.StatusCode.Should().Be(HttpStatusCode.OK);
        cut.Headers.Should().ContainKey(SparqlEndpoints.TruncatedHeader).WhoseValue.Should().Equal("true");
        (await cut.Content.ReadAsStringAsync()).Split("\"s\"").Length.Should().Be(1 + 1 + 3, "the head names ?s once and three rows bind it");
        whole.Headers.Should().NotContainKey(SparqlEndpoints.TruncatedHeader);
    }

    [Fact]
    public async Task A_bad_query_is_400_and_no_query_is_400()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var bad = await client.GetAsync(new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString("SELECT WHERE {"), UriKind.Relative));
        using var none = await client.GetAsync(new Uri("/api/v1/sparql", UriKind.Relative));
        using var update = await client.PostAsync(new Uri("/api/v1/sparql", UriKind.Relative), new StringContent("INSERT DATA { <http://x> <http://y> <http://z> }", Encoding.UTF8, "application/sparql-query"));

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        update.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the endpoint is read-only (R-SEM-8)");
    }

    private static async Task<(string? MediaType, string Body)> GetAsync(HttpClient client, string query, string? accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/sparql?query=" + Uri.EscapeDataString(query), UriKind.Relative));
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return (response.Content.Headers.ContentType?.MediaType, body);
    }
}
