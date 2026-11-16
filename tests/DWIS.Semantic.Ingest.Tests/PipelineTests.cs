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
        var provenance = Provenance.Build(id, "SmartConnect rig signals", "sha256:abc", "{}", report, at, 10, 0);
        var named = new Graph(new UriNode(new Uri(id.ProvenanceGraph)));
        named.Merge(provenance);
        var store = new TripleStore();
        store.Add(named);

        var rows = (SparqlResultSet)new LeviathanQueryProcessor(store).ProcessQuery(new SparqlQueryParser().ParseFromString(Provenance.ListQuery));
        var summary = Provenance.Read(rows.Results.Should().ContainSingle().Subject);

        summary.Should().BeEquivalentTo(new ManifestSummary(id, "SmartConnect rig signals", "sha256:abc", "2026-10-14", at, true, new FindingCounts(0, 1, 0), 10, 0));
        provenance.GetTriplesWithPredicate(provenance.CreateUriNode(new Uri(Provenance.QuarantineGraphProperty))).Should().BeEmpty("an accepted manifest has no quarantine graph");

        var reportRows = (SparqlResultSet)new LeviathanQueryProcessor(store).ProcessQuery(new SparqlQueryParser().ParseFromString(Provenance.ReportQuery(id)));
        var stored = ReportJson.Parse(((ILiteralNode)reportRows.Results.Single()["report"]).Value);
        stored.Should().BeEquivalentTo(report);
    }

    [Fact]
    public void A_rejected_manifest_names_its_quarantine_graph()
    {
        var id = ManifestId.Of("SmartConnect", "x");
        var report = IngestionReport.From(id.Value, "v", [new Finding { Severity = Severity.Error, Message = "e" }]);

        var provenance = Provenance.Build(id, "x", "sha256:0", "{}", report, DateTimeOffset.UnixEpoch, 5, 50);

        provenance.GetTriplesWithPredicate(provenance.CreateUriNode(new Uri(Provenance.QuarantineGraphProperty)))
            .Should().ContainSingle().Which.Object.ToString().Should().Be(id.QuarantineGraph);
    }
}
