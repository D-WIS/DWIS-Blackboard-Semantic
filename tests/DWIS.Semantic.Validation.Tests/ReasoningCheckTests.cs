using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;
using VDS.RDF;

namespace DWIS.Semantic.Validation.Tests;

/// <summary>The re-check after reasoning (Appendix C.6 step 3) with a shape that only fires on an inferred triple.</summary>
public sealed class ReasoningCheckTests : IClassFixture<VocabularyFixture>
{
    private const string Dwis = Namespaces.Canonical;
    private readonly VocabularyFixture _fixture;

    public ReasoningCheckTests(VocabularyFixture fixture)
    {
        _fixture = fixture;
    }

    private static VocabularyModel WithProjectShape()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary");
        var advanced = Path.Combine(root, "advancedDefinitions");
        return VocabularyModel.Load(
            "2026-10-14",
            [
                Path.Combine(root, "DWISVocabulary.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Noun.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Prototypes.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Units.ttl"),
            ],
            [Path.Combine(advanced, "DWISVocabularyShapes.ttl"), Path.Combine(AppContext.BaseDirectory, "Fixtures", "shapes", "after-reasoning.ttl")],
            [Path.Combine(root, "unit-and-quantity-individuals.ttl")]);
    }

    [Fact]
    public void A_shape_on_an_inferred_property_fires_only_after_reasoning_and_only_once()
    {
        var vocabulary = WithProjectShape();
        var prepared = ManifestPreparation.Prepare(Manifests.Load("smartconnect.manifest.json"));
        var graph = ManifestGraphBuilder.Build(prepared);
        var pass = PreprocessingPass.Run(prepared, graph, vocabulary, new PreprocessingOptions { Policy = _fixture.Policy });
        pass.Accepted.Should().BeTrue("no manifest asserts HasValue, so the shape has no target before reasoning");

        // What the reasoner adds: HasValue (super-property of HasDynamicValue) for every data point with a signal.
        var inferred = new Graph();
        var hasValue = inferred.CreateUriNode(new Uri(Dwis + "HasValue"));
        foreach (var t in graph.Graph.GetTriplesWithPredicate(graph.Graph.CreateUriNode(new Uri(Dwis + "HasDynamicValue"))))
        {
            inferred.Assert(new Triple(t.Subject, hasValue, t.Object));
        }

        var findings = ReasoningCheck.Run(graph.Graph, inferred, vocabulary, _fixture.Policy, pass.Findings);

        findings.Should().NotBeEmpty().And.OnlyContain(f => f.Stage == Stage.Commit);
        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        errors.Should().HaveCount(15, "15 of the 254 data points with a signal name no measurable quantity");
        errors.Should().OnlyContain(f => f.Code == FindingCodes.Shacl && f.Source == Dwis + "shapes/ValueNeedsQuantityShape");
        findings.Where(f => f.Code == FindingCodes.DisjointClasses).Should().BeEmpty("the disjointness warnings were reported by the pass already and are not repeated");

        var again = ReasoningCheck.Run(graph.Graph, inferred, vocabulary, _fixture.Policy, pass.Findings.Concat(findings));
        again.Should().BeEmpty("nothing is new the second time");
    }

    [Fact]
    public void Without_inferred_triples_nothing_is_new()
    {
        var prepared = ManifestPreparation.Prepare(Manifests.Load("smartconnect.manifest.json"));
        var graph = ManifestGraphBuilder.Build(prepared);
        var pass = PreprocessingPass.Run(prepared, graph, _fixture.Model, new PreprocessingOptions { Policy = _fixture.Policy });

        ReasoningCheck.Run(graph.Graph, new Graph(), _fixture.Model, _fixture.Policy, pass.Findings).Should().BeEmpty();
    }
}
