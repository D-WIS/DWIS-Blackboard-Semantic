namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The manifest id and the graph names of Appendix C.5 and C.8.</summary>
public sealed class ManifestIdTests
{
    [Theory]
    [InlineData("SmartConnect rig signals", "smartconnect-rig-signals")]
    [InlineData("Units and quantities", "units-and-quantities")]
    [InlineData("  Spaced   Name!! ", "spaced-name")]
    [InlineData("Déjà vu 2", "d-j-vu-2")]
    [InlineData("already-a-slug", "already-a-slug")]
    public void The_name_is_made_safe_for_a_path(string manifestName, string expected)
    {
        ManifestId.Slug(manifestName).Should().Be(expected);
        ManifestId.Of("SmartConnect", manifestName).Name.Should().Be(expected);
    }

    [Fact]
    public void The_id_names_the_four_graphs()
    {
        var id = ManifestId.Of("SmartConnect", "SmartConnect rig signals");

        id.Value.Should().Be("SmartConnect/smartconnect-rig-signals");
        id.AssertedGraph.Should().Be("http://d-wis.org/manifests/SmartConnect/smartconnect-rig-signals");
        id.InferredGraph.Should().Be(id.AssertedGraph + "/inferred");
        id.ProvenanceGraph.Should().Be(id.AssertedGraph + "/provenance");
        id.QuarantineGraph.Should().Be(id.AssertedGraph + "/quarantine");
        id.StagingGraph.Should().Be(id.AssertedGraph + "/staging");
    }

    [Fact]
    public void The_id_reads_back_from_its_text()
    {
        var id = ManifestId.Of("SmartConnect", "SmartConnect rig signals");

        ManifestId.Parse(id.Value).Should().Be(id);
        ManifestId.Parse("SmartConnect").Should().BeNull();
        ManifestId.Parse("SmartConnect/").Should().BeNull();
        ManifestId.Parse("a/b/c").Should().BeNull();
        ManifestId.Parse("SmartConnect/Not A Slug").Should().BeNull();
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("a/b", "x")]
    [InlineData(" Padded", "x")]
    [InlineData("SmartConnect", "")]
    [InlineData("SmartConnect", "!!!")]
    public void A_bad_provider_or_name_is_refused(string provider, string name)
    {
        var act = () => ManifestId.Of(provider, name);

        act.Should().Throw<ArgumentException>();
    }
}
