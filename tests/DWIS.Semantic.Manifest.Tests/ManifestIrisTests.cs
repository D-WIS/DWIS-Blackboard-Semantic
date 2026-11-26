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

    [Fact]
    public void A_name_with_characters_an_iri_cannot_carry_is_stored_with_underscores()
    {
        // Decision D-62: the old server's own CLI example has the provider "DDHub CLI".
        ManifestIris.Provider("DDHub CLI").Should().Be("http://d-wis.org/DDHub_CLI/DDHub_CLI");
        ManifestIris.Node("DDHub CLI", "Hook Load#01").Should().Be("http://d-wis.org/DDHub_CLI/Hook_Load#01");
        ManifestIris.ProvidedVariable("DDHub CLI", "Hook Load").Should().Be("http://d-wis.org/DDHub_CLI/Variables/DDHub_CLI.Hook_Load");
        ManifestIris.InjectedTerm("DDHub CLI", "My Class").Should().Be("http://d-wis.org/DDHub_CLI/Semantic/My_Class");
        ManifestIris.Term("Pressure Drilling Quantity").Should().Be("http://d-wis.org/Pressure_Drilling_Quantity");
        ManifestIris.Provider("PumpStartupManifestPWDa:").Should().Be("http://d-wis.org/PumpStartupManifestPWDa:/PumpStartupManifestPWDa:", "a colon is a character an IRI can carry");
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
