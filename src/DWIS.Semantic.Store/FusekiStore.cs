using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Writing;

namespace DWIS.Semantic.Store;

/// <summary>
/// The Fuseki implementation of <see cref="ITripleStore"/> over HTTP: the Graph Store Protocol at
/// <c>/{dataset}/data</c>, SPARQL query at <c>/{dataset}/sparql</c>, update at <c>/{dataset}/update</c>,
/// the SHACL service at <c>/{dataset}/shacl</c> and the ping at <c>/$/ping</c> (the endpoints of
/// <c>fuseki/config/dwis.ttl</c> in DWIS-Blackboard-Tests). Graphs travel as N-Triples.
/// </summary>
public sealed class FusekiStore : ITripleStore
{
    private const string NTriples = "application/n-triples";
    private const string SparqlResultsJson = "application/sparql-results+json";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly FusekiOptions _options;

    /// <summary>Creates the client over an HTTP client the caller owns (its base address is not used).</summary>
    public FusekiStore(HttpClient http, FusekiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _options = options ?? new FusekiOptions();
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(Url("$/ping"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task PutGraphAsync(Dataset dataset, string graph, IGraph content, CancellationToken cancellationToken = default) =>
        SendGraphAsync(HttpMethod.Put, dataset, graph, content, cancellationToken);

    /// <inheritdoc />
    public Task AddToGraphAsync(Dataset dataset, string graph, IGraph content, CancellationToken cancellationToken = default) =>
        SendGraphAsync(HttpMethod.Post, dataset, graph, content, cancellationToken);

    /// <inheritdoc />
    public async Task<IGraph?> GetGraphAsync(Dataset dataset, string graph, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        using var request = new HttpRequestMessage(HttpMethod.Get, GraphUrl(dataset, graph));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(NTriples));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = new Graph(new UriNode(UriFactory.Create(graph)));
        await ReadGraphAsync(response, result, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteGraphAsync(Dataset dataset, string graph, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        using var response = await _http.DeleteAsync(GraphUrl(dataset, graph), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<QueryResult> QueryAsync(Dataset dataset, string sparql, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sparql);
        options ??= QueryOptions.Default;
        var timeoutMs = ((long)options.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"{_options.PathOf(dataset)}/sparql?timeout={timeoutMs}"))
        {
            Content = new StringContent(sparql, Encoding.UTF8, "application/sparql-query"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SparqlResultsJson));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(NTriples));

        // The store enforces the limit; the client stops waiting a little later in case the connection hangs.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.Timeout + Grace);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new StoreTimeoutException($"The store did not answer the query within {options.Timeout.TotalSeconds:0.#} s.", e);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StoreTimeoutException($"The query hit the time limit of {options.Timeout.TotalSeconds:0.#} s before its first row.", options.Timeout);
                }

                throw new StoreException($"The store answered {(int)response.StatusCode} to the query: {body}", response.StatusCode, body);
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Equals(SparqlResultsJson, StringComparison.OrdinalIgnoreCase))
            {
                var handler = new LimitedResultsHandler(options.RowLimit);
                var cut = await ParseAsync(response, reader => new SparqlJsonParser().Load(handler, reader), () => handler.Results.Count > 0, limit.Token).ConfigureAwait(false);
                return new QueryResult(handler.Results, null, handler.Truncated || cut);
            }

            var graph = new Graph();
            var truncated = await ReadGraphAsync(response, graph, options.RowLimit, limit.Token).ConfigureAwait(false);
            return new QueryResult(null, graph, truncated);
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Dataset dataset, string sparqlUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sparqlUpdate);
        using var content = new StringContent(sparqlUpdate, Encoding.UTF8, "application/sparql-update");
        using var response = await _http.PostAsync(Url($"{_options.PathOf(dataset)}/update"), content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IGraph> ValidateAsync(Dataset dataset, string graph, IGraph shapes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shapes);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"{_options.PathOf(dataset)}/shacl?graph={Uri.EscapeDataString(graph)}"))
        {
            Content = new StringContent(Serialize(shapes), Encoding.UTF8, NTriples),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(NTriples));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var report = new Graph();
        await ReadGraphAsync(response, report, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task SendGraphAsync(HttpMethod method, Dataset dataset, string graph, IGraph content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(content);
        using var request = new HttpRequestMessage(method, GraphUrl(dataset, graph))
        {
            Content = new StringContent(Serialize(content), Encoding.UTF8, NTriples),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static string Serialize(IGraph graph)
    {
        var writer = new System.IO.StringWriter(CultureInfo.InvariantCulture);
        new NTriplesWriter(NTriplesSyntax.Rdf11).Save(graph, writer);
        return writer.ToString();
    }

    /// <summary>Reads the body into the graph, at most <paramref name="limit"/> triples; true when the limit cut it.</summary>
    private static async Task<bool> ReadGraphAsync(HttpResponseMessage response, Graph graph, int limit, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? NTriples;
        var parser = MimeTypesHelper.GetParser(mediaType);
        var handler = new LimitedGraphHandler(graph, limit);
        var cut = await ParseAsync(response, reader => parser.Load(handler, reader), () => graph.Triples.Count > 0, cancellationToken).ConfigureAwait(false);
        return handler.Truncated || cut;
    }

    /// <summary>
    /// Runs the parser on the body. A body cut short by the store's time limit after rows were delivered
    /// counts as a truncated answer (true); any other parse failure is a store error.
    /// </summary>
    private static async Task<bool> ParseAsync(HttpResponseMessage response, Action<TextReader> parse, Func<bool> hasRows, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                parse(reader);
                return false;
            }
            catch (Exception e) when (e is RdfParseException or IOException or OperationCanceledException)
            {
                if (hasRows())
                {
                    return true;
                }

                throw new StoreException($"The store's answer could not be read: {e.Message}", e);
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var request = response.RequestMessage is { } r ? $"{r.Method} {r.RequestUri?.PathAndQuery}" : "the request";
        throw new StoreException($"The store answered {(int)response.StatusCode} to {request}: {body.Trim()}", response.StatusCode, body);
    }

    private Uri Url(string pathAndQuery) => new(_options.BaseUrl, pathAndQuery);

    private Uri GraphUrl(Dataset dataset, string graph) => Url($"{_options.PathOf(dataset)}/data?graph={Uri.EscapeDataString(graph)}");
}
