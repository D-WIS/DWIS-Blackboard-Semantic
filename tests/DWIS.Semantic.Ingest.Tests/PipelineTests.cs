using DWIS.Blackboard.Contracts.Reports;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The pure part of the pipeline (C.2 to C.5) and the provenance graph (C.8), without a store.</summary>
public sealed class PipelineTests : IClassFixture<VocabularyFixture>
{
    private readonly VocabularyFixture _fixture;

    public PipelineTests(VocabularyFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void The_SmartConnect_manifest_is_accepted_whole_under_the_M1_policy()
    {
        var staged = IngestPipeline.Prepare(VocabularyFixture.Manifest("smartconnect.manifest.json"), _fixture.Model, _fixture.Policy);

        staged.Id.Value.Should().Be("SmartConnect/smartconnect-rig-signals");
        staged.Accepted.Should().BeTrue();
        staged.Report.Counts.Errors.Should().Be(0);
        staged.Report.Live.Should().Be(new LiveParts(true, true));
        staged.Report.VocabularyVersion.Should().Be("2026-10-14");
        staged.ContentHash.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        staged.Split.Core.Triples.Count.Should().BeLessThan(staged.Graph.Graph.Triples.Count);
    }

    [Fact]
    public void The_kilogram_copy_is_core_only_with_one_error()
    {
        var staged = IngestPipeline.Prepare(VocabularyFixture.Manifest("smartconnect-kilogram.manifest.json"), _fixture.Model, _fixture.Policy);

        staged.Accepted.Should().BeFalse();
        staged.Report.Counts.Errors.Should().Be(1);
        staged.Report.Live.Should().Be(new LiveParts(true, false));
        staged.Report.Findings[0].Code.Should().Be("unit-quantity-dimension");
    }

    [Fact]
    public void The_hash_depends_on_the_content_not_on_the_formatting()
    {
        var manifest = VocabularyFixture.Manifest("smartconnect.manifest.json");
        var (json, hash) = IngestPipeline.Canonical(manifest);
        var reparsed = Blackboard.Contracts.Manifests.ManifestJson.Parse(json.Replace("\n", "\n   ", StringComparison.Ordinal));

        IngestPipeline.Canonical(reparsed).Hash.Should().Be(hash);
        IngestPipeline.Canonical(VocabularyFixture.Manifest("smartconnect-kilogram.manifest.json")).Hash.Should().NotBe(hash);
    }

    [Fact]
    public void The_provenance_graph_reads_back_through_the_list_query()
    {
        var id = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
        var report = IngestionReport.From(id.Value, "2026-10-14", [new Finding { Severity = Severity.Warning, Message = "w" }], new IngestionStatistics { Asserted = 10 });
        var at = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);
        var provenance = Provenance.Build(id, "SmartConnect rig signals", "sha256:abc", "{}", report, at, 10, 4, 0, "hub:default:7a3c");
        var named = new Graph(new UriNode(new Uri(id.ProvenanceGraph)));
        named.Merge(provenance);
        var store = new TripleStore();
        store.Add(named);

        var rows = (SparqlResultSet)new LeviathanQueryProcessor(store).ProcessQuery(new SparqlQueryParser().ParseFromString(Provenance.ListQuery));
        var summary = Provenance.Read(rows.Results.Should().ContainSingle().Subject);

        summary.Should().BeEquivalentTo(new ManifestSummary(id, "SmartConnect rig signals", "sha256:abc", "2026-10-14", at, true, new FindingCounts(0, 1, 0), 10, 4, 0, "hub:default:7a3c"));
        provenance.GetTriplesWithPredicate(provenance.CreateUriNode(new Uri(Provenance.QuarantineGraphProperty))).Should().BeEmpty("an accepted manifest has no quarantine graph");
        Provenance.Build(id, "x", "sha256:0", "{}", report, at, 1, 0, 0).GetTriplesWithPredicate(provenance.CreateUriNode(new Uri(Provenance.OriginProperty)))
            .Should().ContainSingle().Which.Object.Should().BeAssignableTo<ILiteralNode>().Which.Value.Should().Be("local", "no origin given means injected here");

        var reportRows = (SparqlResultSet)new LeviathanQueryProcessor(store).ProcessQuery(new SparqlQueryParser().ParseFromString(Provenance.ReportQuery(id)));
        var stored = ReportJson.Parse(((ILiteralNode)reportRows.Results.Single()["report"]).Value);
        stored.Should().BeEquivalentTo(report);
    }

    [Fact]
    public void The_subtraction_keeps_what_the_live_data_lacks_and_drops_the_trivia()
    {
        var view = new VDS.RDF.Graph();
        var live = new VDS.RDF.Graph();
        var s = view.CreateUriNode(new Uri("http://d-wis.org/P/x"));
        var a = view.CreateUriNode(new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"));
        view.Assert(new VDS.RDF.Triple(s, a, view.CreateUriNode(new Uri("http://d-wis.org/SPP"))));
        view.Assert(new VDS.RDF.Triple(s, a, view.CreateUriNode(new Uri("http://d-wis.org/PrototypeData"))));
        view.Assert(new VDS.RDF.Triple(s, a, view.CreateUriNode(new Uri("http://www.w3.org/2000/01/rdf-schema#Resource"))));
        view.Assert(new VDS.RDF.Triple(s, a, view.CreateUriNode(new Uri("http://www.w3.org/2002/07/owl#Thing"))));
        view.Assert(new VDS.RDF.Triple(s, a, view.CreateBlankNode()));
        live.Assert(new VDS.RDF.Triple(s, a, live.CreateUriNode(new Uri("http://d-wis.org/SPP"))));

        var trimmed = Inference.Trim(view);

        trimmed.Triples.Should().HaveCount(2).And.Contain(t => t.Object.ToString() == "http://d-wis.org/PrototypeData").And.Contain(t => t.Object.ToString() == "http://d-wis.org/SPP");
        var id = ManifestId.Of("P", "m");
        var rules = Inference.Default.RulesFor(id);
        rules.Should().Contain("DROP SILENT GRAPH <" + id.InferenceScratchGraph + ">").And.Contain("GRAPH <" + id.StagingGraph + ">").And.NotContain("{source}").And.NotContain("{scratch}");
        rules.Should().Contain("dwis:DWISVerb").And.Contain("dwis:DWISNoun", "decision D-64 leaves those out");
        Inference.Default.AnswerFor(id).Should().StartWith("#").And.Contain("CONSTRUCT").And.Contain("!STRENDS(STR(?g), \"/inferred\")");
        var act = () => new Inference("no placeholders", Inference.Default.Answer);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_rejected_manifest_names_its_quarantine_graph()
    {
        var id = ManifestId.Of("SmartConnect", "x");
        var report = IngestionReport.From(id.Value, "v", [new Finding { Severity = Severity.Error, Message = "e" }]);

        var provenance = Provenance.Build(id, "x", "sha256:0", "{}", report, DateTimeOffset.UnixEpoch, 5, 0, 50);

        provenance.GetTriplesWithPredicate(provenance.CreateUriNode(new Uri(Provenance.QuarantineGraphProperty)))
            .Should().ContainSingle().Which.Object.ToString().Should().Be(id.QuarantineGraph);
    }
}
