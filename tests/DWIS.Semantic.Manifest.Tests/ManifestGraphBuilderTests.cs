using DWIS.Blackboard.Contracts.Manifests;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Datasets;

namespace DWIS.Semantic.Manifest.Tests;

/// <summary>Appendix C.3: the asserted graph, and the two milestone 1 queries on it (roadmap F.3).</summary>
public sealed class ManifestGraphBuilderTests
{
    private const string Dwis = "http://d-wis.org/";

    [Fact]
    public void The_SmartConnect_graph_has_the_expected_counts_and_no_legacy_iri()
    {
        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(Fixture.SmartConnect()));

        graph.ProviderIri.Should().Be("http://d-wis.org/SmartConnect/SmartConnect");
        graph.Nodes.Should().Be(391);
        graph.ProvidedVariables.Should().Be(508);
        graph.References.Should().Be(1537 + 254);
        graph.BlankNodes.Should().Be(0, "every class assertion of this manifest uses BelongsToClass");
        graph.Notes.Should().BeEmpty();

        Count(graph.Graph, "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o FILTER (STRSTARTS(STR(?s), 'http://ddhub.no/') || STRSTARTS(STR(?p), 'http://ddhub.no/') || (isIRI(?o) && STRSTARTS(STR(?o), 'http://ddhub.no/'))) }")
            .Should().Be(0, "T-SEM-4: no legacy IRI after translation");
        Count(graph.Graph, $"SELECT (COUNT(DISTINCT ?v) AS ?n) WHERE {{ ?v a <{Dwis}DynamicDrillingSignal> ; <{Dwis}IsProvidedBy> <http://d-wis.org/SmartConnect/SmartConnect> }}").Should().Be(508);
        Count(graph.Graph, $"SELECT (COUNT(DISTINCT ?v) AS ?n) WHERE {{ ?v <{Dwis}IsValidBy> ?f }}").Should().Be(254);
        Count(graph.Graph, $"SELECT (COUNT(*) AS ?n) WHERE {{ <http://d-wis.org/SmartConnect/wobMaxWindow> <{Dwis}Duration> ?d }}").Should().Be(1);
    }

    [Fact]
    public void Both_milestone_queries_return_the_same_signal_on_the_asserted_graph()
    {
        // The unit and quantity individuals come from the built-in "UnitAndQuantity" manifest of today's server
        // (HANDOVER.md); here a two-line stand-in gives the quantity its class.
        var units = new Blackboard.Contracts.Manifests.Manifest
        {
            Provider = new Provider { Name = "UnitAndQuantity", Company = "NORCE" },
            InjectedNodes = [new InjectedNode { UniqueName = "PressureDrillingQuantity", TypeDictionaryURI = "PressureDrillingQuantity" }],
        };
        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(Fixture.SmartConnect())).Graph;
        graph.Merge(ManifestGraphBuilder.Build(ManifestPreparation.Prepare(units)).Graph);

        var first = Select(graph, """
            PREFIX dwis: <http://d-wis.org/>
            SELECT ?signal WHERE { ?dp a dwis:SPP ; dwis:HasDynamicValue ?signal . }
            """);
        var second = Select(graph, """
            PREFIX ddhub: <http://d-wis.org/>
            SELECT ?signal WHERE { ?dp ddhub:IsOfMeasurableQuantity ?q ; ddhub:HasDynamicValue ?signal .
                                   ?q a ddhub:PressureDrillingQuantity . ?dp ddhub:IsLocatedAtEquipment ?e . ?e a ddhub:MudStandpipe . }
            """);

        first.Should().ContainSingle();
        second.Should().ContainSingle();
        second.Single().Should().Be(first.Single());
        first.Single().Should().StartWith("http://d-wis.org/SmartConnect/Variables/SmartConnect.");
    }

    [Fact]
    public void A_reference_to_a_vocabulary_class_becomes_one_blank_node_per_subject_verb_and_class()
    {
        var manifest = Fixture.Small(
            new InjectedReference { Subject = Fixture.Nodes("spp#01"), VerbURI = "http://ddhub.no/IsMeasuredBy", Object = Fixture.Root("http://ddhub.no/PressureSensor") },
            new InjectedReference { Subject = Fixture.Nodes("spp#01"), VerbURI = "http://ddhub.no/IsMeasuredBy", Object = Fixture.Root("http://ddhub.no/PressureSensor") });

        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(manifest));

        graph.BlankNodes.Should().Be(1);
        Count(graph.Graph, $"SELECT (COUNT(*) AS ?n) WHERE {{ <http://d-wis.org/Rig/spp#01> <{Dwis}IsMeasuredBy> ?b . ?b a <{Dwis}PressureSensor> FILTER (isBlank(?b)) }}").Should().Be(1);
    }

    [Fact]
    public void Nodes_variables_fields_and_class_assertions_are_asserted_as_in_C3()
    {
        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(Fixture.Small())).Graph;

        var expected = new Graph();
        new TurtleParser().Load(expected, new StringReader("""
            @prefix dwis: <http://d-wis.org/> .
            @prefix rig: <http://d-wis.org/Rig/> .
            rig:Rig a dwis:DataProvider ; dwis:ProviderName "Rig" .
            <http://d-wis.org/Rig/Variables/Rig.spp> a dwis:DynamicDrillingSignal ; dwis:IsProvidedBy rig:Rig ;
                dwis:IsValidBy <http://d-wis.org/Rig/Variables/Rig.spp_validitySignal> .
            <http://d-wis.org/Rig/Variables/Rig.spp_validitySignal> a dwis:DynamicDrillingSignal ; dwis:IsProvidedBy rig:Rig .
            <http://d-wis.org/Rig/spp#01> a dwis:Measurement , dwis:SPP ; dwis:IsProvidedBy rig:Rig ;
                dwis:HasDynamicValue <http://d-wis.org/Rig/Variables/Rig.spp> ;
                dwis:IsOfMeasurableQuantity <http://d-wis.org/UnitAndQuantity/PressureDrillingQuantity> .
            rig:window a dwis:TimeWindow ; dwis:IsProvidedBy rig:Rig ; dwis:Duration "180.0" .
            """));

        graph.Triples.Count.Should().Be(expected.Triples.Count);
        graph.Difference(expected).AreEqual.Should().BeTrue(graph.Difference(expected).ToString());
    }

    [Fact]
    public void A_datatype_resolver_types_the_field_literals()
    {
        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(Fixture.Small()),
            attribute => attribute == Dwis + "Duration" ? UriFactory.Create("http://www.w3.org/2001/XMLSchema#double") : null).Graph;

        Count(graph, $"SELECT (COUNT(*) AS ?n) WHERE {{ <http://d-wis.org/Rig/window> <{Dwis}Duration> ?d FILTER (datatype(?d) = <http://www.w3.org/2001/XMLSchema#double>) }}").Should().Be(1);
    }

    [Fact]
    public void Bad_references_are_dropped_with_a_note_and_the_rest_is_kept()
    {
        var manifest = Fixture.Small(
            new InjectedReference { Subject = new NodeIdentifier { NameSpace = "Variables", ID = "mirror" }, VerbURI = "http://ddhub.no/HasDynamicValue", Object = Fixture.Nodes("spp#01") },
            new InjectedReference { Subject = new NodeIdentifier { NameSpace = "Elsewhere", ID = "x" }, VerbURI = "http://ddhub.no/IsProvidedBy", Object = Fixture.Nodes("spp#01") },
            new InjectedReference { Subject = Fixture.Nodes("spp#01"), VerbURI = "", Object = Fixture.Nodes("window") });

        var graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(manifest));

        graph.References.Should().Be(3 + 1);
        graph.Notes.Select(n => (n.Code, n.Severity)).Should().BeEquivalentTo(
        [
            (ManifestGraphBuilder.ReferenceToInjectedVariable, NoteSeverity.Warning),
            (ManifestGraphBuilder.UnknownNamespaceAlias, NoteSeverity.Error),
            (ManifestGraphBuilder.IncompleteReference, NoteSeverity.Error),
        ]);
    }

    private static int Count(IGraph graph, string query) => int.Parse(Select(graph, query).Single(), System.Globalization.CultureInfo.InvariantCulture);

    private static List<string> Select(IGraph graph, string query)
    {
        var parsed = new SparqlQueryParser().ParseFromString(query);
        var results = (SparqlResultSet)new LeviathanQueryProcessor(new InMemoryDataset(graph)).ProcessQuery(parsed);
        return results.Select(r => r[0] switch
        {
            ILiteralNode literal => literal.Value,
            IUriNode uri => uri.Uri.AbsoluteUri,
            var other => other.ToString() ?? "",
        }).ToList();
    }
}
