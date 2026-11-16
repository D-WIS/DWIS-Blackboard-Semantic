using DWIS.Blackboard.Contracts;
using DWIS.Semantic.Manifest;
using VDS.RDF;

namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The core-versus-rest split of Appendix C.5 on the SmartConnect manifest.</summary>
public sealed class CoreSplitTests : IClassFixture<VocabularyFixture>
{
    private const string Dwis = Namespaces.Canonical;
    private readonly ManifestGraph _graph;

    public CoreSplitTests(VocabularyFixture fixture)
    {
        _ = fixture;
        _graph = ManifestGraphBuilder.Build(ManifestPreparation.Prepare(VocabularyFixture.Manifest("smartconnect.manifest.json")));
    }

    [Fact]
    public void Core_and_rest_partition_the_asserted_graph()
    {
        var split = CoreSplit.Split(_graph);

        (split.Core.Triples.Count + split.Rest.Triples.Count).Should().Be(_graph.Graph.Triples.Count);
        split.Core.Triples.Should().NotIntersectWith(split.Rest.Triples);
        split.Core.Triples.Count.Should().BeGreaterThan(0);
        split.Rest.Triples.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_core_holds_the_provider_the_signals_their_flags_and_their_data_points()
    {
        var split = CoreSplit.Split(_graph);
        var core = split.Core;
        var provider = core.CreateUriNode(new Uri(_graph.ProviderIri));
        var signalClass = core.CreateUriNode(new Uri(Vocabulary.DynamicDrillingSignal));
        var rdfType = core.CreateUriNode(new Uri(Vocabulary.RdfType));

        core.GetTriplesWithSubject(provider).Should().NotBeEmpty("the provider and its name are core");
        core.GetTriplesWithPredicateObject(rdfType, signalClass).Should().HaveCount(254 * 2, "every provided variable and its validity flag is a signal");
        core.GetTriplesWithPredicate(core.CreateUriNode(new Uri(Vocabulary.IsValidBy))).Should().HaveCount(254);
        core.GetTriplesWithPredicate(core.CreateUriNode(new Uri(CoreSplit.HasDynamicValue))).Should().NotBeEmpty("data points reach their signal through HasDynamicValue");

        // The standpipe pressure data point is core with its type; its unit and quantity are not.
        var sppDataPoint = core.CreateUriNode(new Uri(Dwis + "SmartConnect/mainwellCirculationCommon_pressureStandPipe#01"));
        core.GetTriplesWithSubjectPredicate(sppDataPoint, rdfType).Should().ContainSingle(t => t.Object.ToString() == Dwis + "SPP");
        core.GetTriplesWithPredicate(core.CreateUriNode(new Uri(Dwis + "HasUnitOfMeasure"))).Should().BeEmpty();
        core.GetTriplesWithPredicate(core.CreateUriNode(new Uri(Dwis + "IsOfMeasurableQuantity"))).Should().BeEmpty();
        split.Rest.GetTriplesWithPredicate(split.Rest.CreateUriNode(new Uri(Dwis + "HasUnitOfMeasure"))).Should().NotBeEmpty();
    }

    [Fact]
    public void The_core_has_no_blank_nodes()
    {
        var split = CoreSplit.Split(_graph);

        split.Core.Triples.Where(t => t.Object.NodeType == NodeType.Blank || t.Subject.NodeType == NodeType.Blank).Should().BeEmpty();
    }
}
