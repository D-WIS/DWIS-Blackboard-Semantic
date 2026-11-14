using VDS.RDF;
using VDS.RDF.Parsing.Handlers;
using VDS.RDF.Query;

namespace DWIS.Semantic.Store;

/// <summary>Collects the rows of a SELECT or the boolean of an ASK, stopping the parser at the row limit (R-SEM-8).</summary>
internal sealed class LimitedResultsHandler : BaseResultsHandler
{
    private readonly ResultSetHandler _inner;
    private readonly int _limit;
    private int _rows;

    public LimitedResultsHandler(int limit)
    {
        _limit = limit;
        _inner = new ResultSetHandler(Results);
    }

    public SparqlResultSet Results { get; } = new();

    /// <summary>True when the limit stopped the reading; the store may have had more rows.</summary>
    public bool Truncated { get; private set; }

    protected override void StartResultsInternal() => _inner.StartResults();

    protected override void EndResultsInternal(bool ok) => _inner.EndResults(ok);

    protected override void HandleBooleanResultInternal(bool result) => _inner.HandleBooleanResult(result);

    protected override bool HandleVariableInternal(string var) => _inner.HandleVariable(var);

    protected override bool HandleResultInternal(ISparqlResult result)
    {
        if (_rows >= _limit)
        {
            Truncated = true;
            return false;
        }

        _rows++;
        return _inner.HandleResult(result);
    }
}

/// <summary>Collects triples into a graph, stopping the parser at the triple limit (R-SEM-8).</summary>
internal sealed class LimitedGraphHandler : BaseRdfHandler
{
    private readonly IGraph _graph;
    private readonly int _limit;
    private int _count;

    public LimitedGraphHandler(IGraph graph, int limit)
        : base(graph)
    {
        _graph = graph;
        _limit = limit;
    }

    public override bool AcceptsAll => true;

    /// <summary>True when the limit stopped the reading; the store may have had more triples.</summary>
    public bool Truncated { get; private set; }

    protected override bool HandleTripleInternal(Triple t)
    {
        if (_count >= _limit)
        {
            Truncated = true;
            return false;
        }

        _graph.Assert(t);
        _count++;
        return true;
    }

    protected override bool HandleQuadInternal(Triple t, IRefNode graph) => HandleTripleInternal(t);

    protected override bool HandleNamespaceInternal(string prefix, Uri namespaceUri)
    {
        _graph.NamespaceMap.AddNamespace(prefix, namespaceUri);
        return true;
    }

    protected override bool HandleBaseUriInternal(Uri baseUri)
    {
        _graph.BaseUri = baseUri;
        return true;
    }
}
