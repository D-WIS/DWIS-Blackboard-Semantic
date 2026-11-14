using VDS.RDF;
using VDS.RDF.Parsing;
using Xunit;

namespace DWIS.Semantic.Store.Tests;

/// <summary>The row and triple limits (R-SEM-8), without a store.</summary>
public sealed class LimitedHandlersTests
{
    private const string Rows = """
        { "head": { "vars": ["s"] },
          "results": { "bindings": [
            { "s": { "type": "uri", "value": "http://d-wis.org/a" } },
            { "s": { "type": "uri", "value": "http://d-wis.org/b" } },
            { "s": { "type": "uri", "value": "http://d-wis.org/c" } } ] } }
        """;

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(10, 3, false)]
    public void Rows_stop_at_the_limit(int limit, int expected, bool truncated)
    {
        var handler = new LimitedResultsHandler(limit);

        new SparqlJsonParser().Load(handler, new StringReader(Rows));

        handler.Results.Count.Should().Be(expected);
        handler.Results.Variables.Should().Equal("s");
        handler.Truncated.Should().Be(truncated);
    }

    [Fact]
    public void A_boolean_answer_is_kept()
    {
        var handler = new LimitedResultsHandler(1);

        new SparqlJsonParser().Load(handler, new StringReader("""{ "head": {}, "boolean": true }"""));

        handler.Results.Result.Should().BeTrue();
        handler.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(4, 4, false)]
    public void Triples_stop_at_the_limit(int limit, int expected, bool truncated)
    {
        var graph = new Graph();
        var handler = new LimitedGraphHandler(graph, limit);

        new NTriplesParser().Load(handler, new StringReader("""
            <http://d-wis.org/a> <http://d-wis.org/p> <http://d-wis.org/b> .
            <http://d-wis.org/b> <http://d-wis.org/p> <http://d-wis.org/c> .
            <http://d-wis.org/c> <http://d-wis.org/p> <http://d-wis.org/d> .
            <http://d-wis.org/d> <http://d-wis.org/p> <http://d-wis.org/e> .
            """));

        graph.Triples.Count.Should().Be(expected);
        handler.Truncated.Should().Be(truncated);
    }

    [Fact]
    public void Datasets_map_to_the_configured_paths()
    {
        var options = new FusekiOptions();

        options.PathOf(Dataset.Live).Should().Be("dwis");
        options.PathOf(Dataset.Quarantine).Should().Be("dwis-quarantine");
        options.PathOf(Dataset.Inference).Should().Be("dwis-inf");
    }
}
