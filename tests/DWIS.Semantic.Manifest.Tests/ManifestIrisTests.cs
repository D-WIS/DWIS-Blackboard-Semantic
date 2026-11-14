namespace DWIS.Semantic.Manifest.Tests;

/// <summary>The IRI scheme of Appendix C.3, unchanged from today's server apart from the namespace.</summary>
public sealed class ManifestIrisTests
{
    [Fact]
    public void The_scheme_matches_todays_server()
    {
        ManifestIris.Provider("SmartConnect").Should().Be("http://d-wis.org/SmartConnect/SmartConnect");
        ManifestIris.Node("SmartConnect", "spp#01").Should().Be("http://d-wis.org/SmartConnect/spp#01");
        ManifestIris.ProvidedVariable("SmartConnect", "spp").Should().Be("http://d-wis.org/SmartConnect/Variables/SmartConnect.spp");
        ManifestIris.InjectedTerm("SmartConnect", "MyClass").Should().Be("http://d-wis.org/SmartConnect/Semantic/MyClass");
        ManifestIris.ValidityFlagId("spp").Should().Be("spp_validitySignal");
    }

    [Theory]
    [InlineData("Measurement", "http://d-wis.org/Measurement")]
    [InlineData("http://ddhub.no/Measurement", "http://d-wis.org/Measurement")]
    [InlineData("http://d-wis.org/Measurement", "http://d-wis.org/Measurement")]
    [InlineData("http://www.w3.org/2002/07/owl#Thing", "http://www.w3.org/2002/07/owl#Thing")]
    public void Terms_are_canonical(string given, string expected)
    {
        ManifestIris.Term(given).Should().Be(expected);
    }
}
