using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Semantic.Manifest;

namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The staging table of the two-phase injection (Appendix C.5), the variables to create and the endpoints graph, without a store.</summary>
public sealed class StagingTableTests : IClassFixture<VocabularyFixture>
{
    private const string Part = "small/smartconnect-01.manifest.json";
    private readonly VocabularyFixture _fixture;

    public StagingTableTests(VocabularyFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void A_staged_manifest_waits_for_its_commit_or_abort_and_expires_after_the_time_limit()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var table = new StagingTable(clock, TimeSpan.FromSeconds(60));
        var staged = IngestPipeline.Prepare(VocabularyFixture.Manifest(Part), _fixture.Model);

        var entry = table.Stage(staged, "local");
        entry.Origin.Should().Be("local");
        entry.ExpiresAt.Should().Be(clock.Now + TimeSpan.FromSeconds(60));
        table.Count.Should().Be(1);
        table.Find(staged.Id).Should().BeSameAs(entry);

        // Staged again: the entry is replaced, not doubled.
        var again = table.Stage(staged, "local");
        table.Count.Should().Be(1);
        table.Find(staged.Id).Should().BeSameAs(again);

        // Taken for its commit: gone from the table.
        table.Take(staged.Id).Should().BeSameAs(again);
        table.Take(staged.Id).Should().BeNull();
        table.Count.Should().Be(0);

        // Aborted: gone; a second abort says so.
        table.Stage(staged, "local");
        table.Abort(staged.Id).Should().BeTrue();
        table.Abort(staged.Id).Should().BeFalse();

        // Expired: gone without a commit, on the first look after the limit.
        table.Stage(staged, "local");
        clock.Now += TimeSpan.FromSeconds(59);
        table.Find(staged.Id).Should().NotBeNull();
        clock.Now += TimeSpan.FromSeconds(1);
        table.Find(staged.Id).Should().BeNull();
        table.Take(staged.Id).Should().BeNull();
        table.Count.Should().Be(0);
    }

    [Fact]
    public void The_variables_to_create_carry_their_validity_flags()
    {
        var manifest = VocabularyFixture.Manifest(Part);
        var variables = IngestPipeline.VariablesToCreate(manifest);
        variables.Should().HaveCount(20, "ten variables and the ten flags added for them");
        var withFlag = variables.Where(v => v.ValidityVariableId is not null).ToList();
        withFlag.Should().HaveCount(10);
        withFlag.Should().OnlyContain(v => v.ValidityVariableId == v.VariableId + ManifestIris.ValiditySuffix);
        variables.Where(v => v.ValidityVariableId is null).Should().OnlyContain(v => v.VariableId.EndsWith(ManifestIris.ValiditySuffix, StringComparison.Ordinal) && v.DataType == "bool");
        variables.Select(v => v.VariableId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_endpoints_graph_is_keyed_by_the_variable_iri_and_dropped_by_it()
    {
        var manifest = VocabularyFixture.Manifest(Part);
        var id = ManifestId.Of(manifest);
        var endpoints = new List<VariableEndpoint>
        {
            new("circulationBaseOil10s_volume", "opc.tcp://rig:4840", "http://ddhub.no/SmartConnect-01/Variables/", "SmartConnect-01.circulationBaseOil10s_volume"),
            new("circulationBaseOil10s_volume_validitySignal", "opc.tcp://rig:4840", "http://ddhub.no/SmartConnect-01/Variables/", "SmartConnect-01.circulationBaseOil10s_volume_validitySignal"),
        };
        var graph = Endpoints.Build(id, id.Provider, endpoints);
        graph.Triples.Count.Should().Be(8, "four triples per endpoint");
        var variable = ManifestIris.ProvidedVariable("SmartConnect-01", "circulationBaseOil10s_volume");
        graph.GetTriplesWithSubject(new Uri(variable)).Should().HaveCount(4);

        var iris = Endpoints.VariableIris(manifest);
        iris.Should().HaveCount(20).And.Contain(variable).And.Contain(variable + ManifestIris.ValiditySuffix);
        Endpoints.DeleteUpdate(iris).Should().StartWith("WITH <" + Endpoints.Graph + "> DELETE").And.Contain("<" + variable + ">");
        Endpoints.DeleteUpdate([]).Should().BeEmpty();
        Endpoints.Query(id).Should().Contain("\"" + id.Value + "\"");
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
