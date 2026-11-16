using System.Net.Mime;
using DWIS.Blackboard.Contracts;
using DWIS.Semantic.Store;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using VDS.RDF;
using VDS.RDF.Parsing;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;
using VDS.RDF.Query;
using VDS.RDF.Writing;

namespace DWIS.Semantic.Host.Endpoints;

/// <summary>
/// <c>/api/v1/sparql</c>: the SPARQL 1.1 protocol, read-only, with the time and row limits of the
/// configuration (R-SEM-8). <c>GET ?query=</c>, <c>POST</c> with <c>application/sparql-query</c> or a form
/// field <c>query</c>. Answers follow <c>Accept</c>: SPARQL results JSON (default), CSV or XML for tables;
/// Turtle (default) or N-Triples for graphs. <c>http://ddhub.no/</c> in the query is read as
/// <c>http://d-wis.org/</c> (specification 3.6). An answer cut by a limit carries <c>DWIS-Truncated: true</c>.
/// </summary>
public static class SparqlEndpoints
{
    /// <summary>The header set when the row or time limit cut the answer short.</summary>
    public const string TruncatedHeader = "DWIS-Truncated";

    private const string ResultsJson = "application/sparql-results+json";
    private const string ResultsXml = "application/sparql-results+xml";
    private const string Csv = "text/csv";
    private const string Turtle = "text/turtle";
    private const string NTriples = "application/n-triples";

    /// <summary>Maps the routes.</summary>
    public static RouteGroupBuilder MapSparql(this RouteGroupBuilder api)
    {
        api.MapGet("/sparql", (HttpRequest request, ITripleStore store, IOptions<QueryLimits> limits, CancellationToken cancellationToken) =>
            RunAsync(request.Query["query"].ToString(), request, store, limits.Value, cancellationToken));
        api.MapPost("/sparql", async (HttpRequest request, ITripleStore store, IOptions<QueryLimits> limits, CancellationToken cancellationToken) =>
        {
            string query;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                query = form["query"].ToString();
            }
            else
            {
                using var reader = new StreamReader(request.Body);
                query = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            return await RunAsync(query, request, store, limits.Value, cancellationToken).ConfigureAwait(false);
        }).Accepts<string>("application/sparql-query", "application/x-www-form-urlencoded");
        return api;
    }

    /// <summary>
    /// Reads every legacy IRI in the query as canonical (specification 3.6: both prefixes work). Only IRI
    /// tokens (<c>&lt;http://ddhub.no/…&gt;</c>, in PREFIX lines and in patterns) are rewritten; string
    /// literals are left alone, so a FILTER on the text of an IRI still means what it says.
    /// </summary>
    public static string Canonical(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Replace("<" + Namespaces.Legacy, "<" + Namespaces.Canonical, StringComparison.Ordinal);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(string? query, HttpRequest request, ITripleStore store, QueryLimits limits, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return TypedResults.Problem("Give the query as ?query= on GET, or as the body (application/sparql-query) or the form field query on POST.", statusCode: StatusCodes.Status400BadRequest, title: "No query");
        }

        var accept = request.GetTypedHeaders().Accept;
        QueryResult result;
        try
        {
            result = await store.QueryAsync(Dataset.Live, Canonical(query), limits.ToQueryOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (StoreTimeoutException e)
        {
            return TypedResults.Problem($"The query did not finish within {e.Limit.TotalSeconds:0} s (R-SEM-8).", statusCode: StatusCodes.Status504GatewayTimeout, title: "Query timeout");
        }
        catch (StoreException e) when (e.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return TypedResults.Problem(e.Body ?? e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Bad query");
        }
        catch (StoreException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "Store error");
        }

        if (result.Truncated)
        {
            request.HttpContext.Response.Headers[TruncatedHeader] = "true";
        }

        return result.Table is not null ? Table(result.Table, accept) : GraphAnswer(result.Graph!, accept);
    }

    private static ContentHttpResult Table(SparqlResultSet table, IList<MediaTypeHeaderValue> accept)
    {
        var (writer, mediaType) = Wanted(accept, Csv) ? (new SparqlCsvWriter(), Csv)
            : Wanted(accept, ResultsXml) ? (new SparqlXmlWriter(), ResultsXml)
            : ((ISparqlResultsWriter)new SparqlJsonWriter(), ResultsJson);
        using var text = new System.IO.StringWriter();
        writer.Save(table, text);
        return TypedResults.Content(text.ToString(), mediaType);
    }

    private static ContentHttpResult GraphAnswer(IGraph graph, IList<MediaTypeHeaderValue> accept)
    {
        var (writer, mediaType) = Wanted(accept, NTriples) ? (new NTriplesWriter(NTriplesSyntax.Rdf11), NTriples)
            : ((IRdfWriter)new CompressingTurtleWriter(), Turtle);
        using var text = new System.IO.StringWriter();
        writer.Save(graph, text);
        return TypedResults.Content(text.ToString(), mediaType);
    }

    private static bool Wanted(IList<MediaTypeHeaderValue> accept, string mediaType) =>
        accept.Any(a => string.Equals(a.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase));
}
