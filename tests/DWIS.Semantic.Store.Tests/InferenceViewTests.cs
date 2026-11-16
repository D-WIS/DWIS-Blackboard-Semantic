using VDS.RDF;
using VDS.RDF.Parsing;
using Xunit;

namespace DWIS.Semantic.Store.Tests;

/// <summary>
/// The inference view <c>/dwis-inf</c> (milestone 2, M2.1): a reasoner over the union of the live data,
/// query only, that sees writes made through the live dataset without a restart.
/// </summary>
[Collection(SharedFuseki.Name)]
public sealed class InferenceViewTests
{
    private const string Dwis = "http://d-wis.org/";
    private readonly FusekiFixture _fuseki;

    public InferenceViewTests(FusekiFixture fuseki)
    {
        _fuseki = fuseki;
    }

    [Fact]
    public async Task The_view_entails_a_superclass_from_a_graph_written_after_start_up()
    {
        _fuseki.SkipUnlessAvailable();
        var store = _fuseki.Store;
        var vocabulary = $"{Dwis}test/{Guid.NewGuid()}";
        var data = $"{Dwis}test/{Guid.NewGuid()}";
        try
        {
            var schema = new Graph();
            new TurtleParser().Load(schema, new StringReader($"""
                @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
                @prefix owl: <http://www.w3.org/2002/07/owl#> .
                <{Dwis}TestSPP> a owl:Class ; rdfs:subClassOf <{Dwis}TestPrototype> .
                <{Dwis}TestPrototype> a owl:Class .
                """));
            var instances = new Graph();
            new TurtleParser().Load(instances, new StringReader($"<{Dwis}TestRig/spp> a <{Dwis}TestSPP> ."));
            await store.PutGraphAsync(Dataset.Live, vocabulary, schema);
            await store.PutGraphAsync(Dataset.Live, data, instances);

            var live = await store.QueryAsync(Dataset.Live, $"ASK {{ <{Dwis}TestRig/spp> a <{Dwis}TestPrototype> }}");
            var inferred = await store.QueryAsync(Dataset.Inference, $"ASK {{ <{Dwis}TestRig/spp> a <{Dwis}TestPrototype> }}");
            var construct = await store.QueryAsync(Dataset.Inference, $"CONSTRUCT {{ ?s ?p ?o }} WHERE {{ ?s ?p ?o . FILTER (STRSTARTS(STR(?s), \"{Dwis}TestRig/\")) }}");

            live.Table!.Result.Should().BeFalse("the live dataset holds only what was asserted");
            inferred.Table!.Result.Should().BeTrue("the view reasons over the live union, including graphs written after it started");
            construct.Graph!.Triples.Should().Contain(t => t.Object.ToString() == Dwis + "TestPrototype");
        }
        finally
        {
            await store.DeleteGraphAsync(Dataset.Live, vocabulary);
            await store.DeleteGraphAsync(Dataset.Live, data);
        }
    }

    [Fact]
    public async Task The_view_refuses_updates()
    {
        _fuseki.SkipUnlessAvailable();

        var act = () => _fuseki.Store.UpdateAsync(Dataset.Inference, $"INSERT DATA {{ <{Dwis}test/x> <{Dwis}test/p> <{Dwis}test/y> }}");

        await act.Should().ThrowAsync<StoreException>("the view has query endpoints only; writes go through the live dataset");
    }
}
