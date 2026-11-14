using System.Net;
using VDS.RDF;
using VDS.RDF.Parsing;
using Xunit;

namespace DWIS.Semantic.Store.Tests;

/// <summary>The contract of <see cref="ITripleStore"/> against a real Fuseki with the Rig Edition datasets (Appendix G.1; T-SEM-2, T-SEM-10).</summary>
[Collection(SharedFuseki.Name)]
public sealed class FusekiStoreTests
{
    private const string Dwis = "http://d-wis.org/";
    private readonly FusekiFixture _f;
    private readonly ITestOutputHelper _output;

    public FusekiStoreTests(FusekiFixture fixture, ITestOutputHelper output)
    {
        _f = fixture;
        _output = output;
    }

    private FusekiStore Store => _f.Store;

    [Fact]
    public async Task Ping_answers_true_for_a_running_store_and_false_for_none()
    {
        _f.SkipUnlessAvailable();
        _output.WriteLine($"Fuseki from {_f.Source} at {_f.BaseUrl}");

        (await Store.PingAsync()).Should().BeTrue();
        var nobody = new FusekiStore(_f.Http, new FusekiOptions { BaseUrl = new Uri("http://127.0.0.1:1/") });
        (await nobody.PingAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_graph_is_written_read_back_whole_and_deleted()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            var written = SampleGraph(3);
            await Store.PutGraphAsync(Dataset.Live, name, written);

            var read = await Store.GetGraphAsync(Dataset.Live, name);
            read.Should().NotBeNull();
            read!.Triples.Count.Should().Be(3);
            read.Triples.Should().BeEquivalentTo(written.Triples);
            read.Name.Should().BeOfType<UriNode>().Which.Uri.AbsoluteUri.Should().Be(name);

            (await Store.DeleteGraphAsync(Dataset.Live, name)).Should().BeTrue();
            (await Store.GetGraphAsync(Dataset.Live, name)).Should().BeNull();
            (await Store.DeleteGraphAsync(Dataset.Live, name)).Should().BeFalse("the graph is already gone");
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task Put_replaces_and_add_appends()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(5));
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(2));
            (await Store.GetGraphAsync(Dataset.Live, name))!.Triples.Count.Should().Be(2, "PUT replaces");

            await Store.AddToGraphAsync(Dataset.Live, name, SampleGraph(4, first: 100));
            (await Store.GetGraphAsync(Dataset.Live, name))!.Triples.Count.Should().Be(6, "POST appends");
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task Literals_with_datatypes_and_languages_survive_the_round_trip()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            var g = new Graph();
            var s = g.CreateUriNode(UriFactory.Create(Dwis + "Rig/spp"));
            g.Assert(s, g.CreateUriNode(UriFactory.Create(Dwis + "L")), g.CreateLiteralNode("-1", UriFactory.Create(XmlSpecsHelper.XmlSchemaDataTypeInteger)));
            g.Assert(s, g.CreateUriNode(UriFactory.Create("http://www.w3.org/2000/01/rdf-schema#label")), g.CreateLiteralNode("Standpipe pressure", "en"));
            g.Assert(s, g.CreateUriNode(UriFactory.Create(Dwis + "Note")), g.CreateLiteralNode("line one\nline \"two\" é"));
            await Store.PutGraphAsync(Dataset.Live, name, g);

            var read = await Store.GetGraphAsync(Dataset.Live, name);
            read!.Triples.Should().BeEquivalentTo(g.Triples);
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task Select_and_ask_fill_the_table_and_the_union_default_graph_sees_named_graphs()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(7));

            var count = await Store.QueryAsync(Dataset.Live, $"SELECT (COUNT(*) AS ?n) WHERE {{ GRAPH <{name}> {{ ?s ?p ?o }} }}");
            count.Graph.Should().BeNull();
            count.Truncated.Should().BeFalse();
            count.Table!.Count.Should().Be(1);
            ((ILiteralNode)count.Table[0]["n"]).Value.Should().Be("7");

            // No GRAPH clause: the union default graph of /dwis (specification 3.7).
            var ask = await Store.QueryAsync(Dataset.Live, $"ASK {{ <{Dwis}Rig/node0> ?p ?o }}");
            ask.Table!.Result.Should().BeTrue();
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task Construct_fills_the_graph()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(4));

            var result = await Store.QueryAsync(Dataset.Live, $"CONSTRUCT {{ ?s ?p ?o }} WHERE {{ GRAPH <{name}> {{ ?s ?p ?o }} }}");

            result.Table.Should().BeNull();
            result.Graph!.Triples.Count.Should().Be(4);
            result.Truncated.Should().BeFalse();
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task The_row_limit_truncates_select_and_construct_answers()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(100));
            var limit = new QueryOptions { RowLimit = 10 };

            var rows = await Store.QueryAsync(Dataset.Live, $"SELECT ?s ?p ?o WHERE {{ GRAPH <{name}> {{ ?s ?p ?o }} }}", limit);
            rows.Table!.Count.Should().Be(10);
            rows.Truncated.Should().BeTrue();

            var triples = await Store.QueryAsync(Dataset.Live, $"CONSTRUCT {{ ?s ?p ?o }} WHERE {{ GRAPH <{name}> {{ ?s ?p ?o }} }}", limit);
            triples.Graph!.Triples.Count.Should().Be(10);
            triples.Truncated.Should().BeTrue();

            var exact = await Store.QueryAsync(Dataset.Live, $"SELECT ?s ?p ?o WHERE {{ GRAPH <{name}> {{ ?s ?p ?o }} }}", new QueryOptions { RowLimit = 100 });
            exact.Table!.Count.Should().Be(100);
            exact.Truncated.Should().BeFalse("exactly the limit is not a truncation");
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task The_time_limit_cancels_a_long_query()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(300));
            // A four-way cross product of 300 triples, counted: billions of rows, nothing streamed before the end.
            var slow = $"SELECT (COUNT(*) AS ?n) WHERE {{ GRAPH <{name}> {{ ?a ?b ?c . ?d ?e ?f . ?g ?h ?i . ?j ?k ?l }} }}";

            var act = () => Store.QueryAsync(Dataset.Live, slow, new QueryOptions { Timeout = TimeSpan.FromMilliseconds(300) });

            var e = await act.Should().ThrowAsync<StoreTimeoutException>();
            _output.WriteLine(e.Which.Message);
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task A_bad_query_is_a_store_error_with_the_status_and_the_body()
    {
        _f.SkipUnlessAvailable();

        var act = () => Store.QueryAsync(Dataset.Live, "SELECT WHERE { this is not SPARQL");

        var e = await act.Should().ThrowAsync<StoreException>();
        e.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        e.Which.Body.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_update_drops_and_inserts_in_one_request()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Live, name, SampleGraph(5));

            await Store.UpdateAsync(Dataset.Live, $"DROP SILENT GRAPH <{name}>; INSERT DATA {{ GRAPH <{name}> {{ <{Dwis}Rig/a> <{Dwis}p> <{Dwis}Rig/b> }} }}");

            var read = await Store.GetGraphAsync(Dataset.Live, name);
            read!.Triples.Count.Should().Be(1);
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    [Fact]
    public async Task The_quarantine_dataset_is_invisible_to_live_queries()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            await Store.PutGraphAsync(Dataset.Quarantine, name, SampleGraph(2));

            (await Store.QueryAsync(Dataset.Quarantine, $"ASK {{ GRAPH <{name}> {{ ?s ?p ?o }} }}")).Table!.Result.Should().BeTrue();
            (await Store.QueryAsync(Dataset.Live, $"ASK {{ GRAPH <{name}> {{ ?s ?p ?o }} }}")).Table!.Result.Should().BeFalse("T-SEM-10");
            (await Store.GetGraphAsync(Dataset.Live, name)).Should().BeNull();
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Quarantine, name);
        }
    }

    [Fact]
    public async Task The_shacl_endpoint_validates_one_graph_against_shapes()
    {
        _f.SkipUnlessAvailable();
        var name = TestGraph();
        try
        {
            var data = new Graph();
            data.NamespaceMap.AddNamespace("dwis", UriFactory.Create(Dwis));
            new TurtleParser().Load(data, new StringReader($"""
                @prefix dwis: <{Dwis}> .
                <{Dwis}Rig/spp> a dwis:DrillingSignal .
                <{Dwis}Rig/wob> a dwis:DrillingSignal ; dwis:HasUnitOfMeasure <{Dwis}UnitAndQuantity/Bar> .
                """));
            await Store.PutGraphAsync(Dataset.Live, name, data);
            var shapes = new Graph();
            new TurtleParser().Load(shapes, new StringReader($"""
                @prefix sh: <http://www.w3.org/ns/shacl#> .
                @prefix dwis: <{Dwis}> .
                <{Dwis}shapes/UnitShape> a sh:NodeShape ; sh:targetClass dwis:DrillingSignal ;
                    sh:property [ sh:path dwis:HasUnitOfMeasure ; sh:minCount 1 ; sh:message "A signal needs a unit." ] .
                """));

            var report = await Store.ValidateAsync(Dataset.Live, name, shapes);

            var sh = "http://www.w3.org/ns/shacl#";
            var conforms = report.GetTriplesWithPredicate(report.CreateUriNode(UriFactory.Create(sh + "conforms"))).Single();
            ((ILiteralNode)conforms.Object).Value.Should().Be("false");
            var results = report.GetTriplesWithPredicate(report.CreateUriNode(UriFactory.Create(sh + "focusNode"))).ToList();
            results.Should().HaveCount(1);
            ((IUriNode)results[0].Object).Uri.AbsoluteUri.Should().Be(Dwis + "Rig/spp");
        }
        finally
        {
            await Store.DeleteGraphAsync(Dataset.Live, name);
        }
    }

    private static string TestGraph() => $"{Dwis}test/{Guid.NewGuid():N}";

    private static Graph SampleGraph(int triples, int first = 0)
    {
        var g = new Graph();
        var p = g.CreateUriNode(UriFactory.Create(Dwis + "IsSubPartOf"));
        for (var i = first; i < first + triples; i++)
        {
            g.Assert(g.CreateUriNode(UriFactory.Create($"{Dwis}Rig/node{i}")), p, g.CreateUriNode(UriFactory.Create($"{Dwis}Rig/node{i + 1}")));
        }

        return g;
    }
}
