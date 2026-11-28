namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The reasoning rules file (decision D-65): every rule listed, on or off, with a description; a typo is an error when read.</summary>
public sealed class InferenceRulesTests
{
    [Fact]
    public void The_embedded_rules_list_every_rule_with_a_description_and_a_switch()
    {
        var rules = Inference.Default.Rules;

        rules.Rules.Select(r => r.Name).Should().Equal("sub-property", "inverse", "domain", "range", "some-values-from", "sub-class");
        rules.Rules.Should().OnlyContain(r => r.Description.Length > 40 && r.Jena != null && r.Update.Contains("{scratch}"));
        rules.Enabled.Select(r => r.Name).Should().Equal("sub-property", "inverse", "some-values-from", "sub-class");
        rules.Disabled.Select(r => r.Name).Should().Equal(["domain", "range"], "decision D-65: the domain and range axioms are not trusted");
        rules["domain"]!.Jena.Should().Be("rdfs2");
        rules["nothing"].Should().BeNull();
    }

    [Fact]
    public void The_update_text_holds_the_enabled_rules_in_order_and_nothing_else()
    {
        var text = Inference.Default.Rules.UpdateText();

        text.Should().StartWith("PREFIX rdf:").And.Contain("# sub-property").And.Contain("# sub-class").And.NotContain("# domain").And.NotContain("rdfs:domain");
        text.IndexOf("# inverse", StringComparison.Ordinal).Should().BeGreaterThan(text.IndexOf("# sub-property", StringComparison.Ordinal));
        text.Split("INSERT").Should().HaveCount(6, "five updates: four rules, the some-values-from rule being two updates");

        var id = ManifestId.Of("P", "m");
        var request = Inference.Default.RulesFor(id);
        request.Should().StartWith("DROP SILENT GRAPH <" + id.InferenceScratchGraph + "> ;").And.NotContain("{source}").And.NotContain("{scratch}");
    }

    [Fact]
    public void A_rule_can_be_switched_on_or_off_by_the_file()
    {
        var rules = InferenceRules.Parse("""
            {
              "prefixes": ["PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>"],
              "rules": [
                { "name": "domain", "enabled": true, "description": "The subject gets the domain class.", "update": ["INSERT { GRAPH <{scratch}> { ?s a ?c } }", "WHERE { GRAPH <{source}> { ?s ?p ?o } ?p rdfs:domain ?c }"] },
                { "name": "sub-class", "enabled": false, "description": "Broader classes.", "update": ["INSERT { GRAPH <{scratch}> { ?s a ?d } } WHERE { GRAPH <{source}> { ?s a ?c } ?c rdfs:subClassOf ?d }"] },
              ]
            }
            """);

        rules.Enabled.Select(r => r.Name).Should().Equal("domain");
        rules.UpdateText().Should().Contain("rdfs:domain").And.NotContain("subClassOf");
        var none = InferenceRules.Parse("""{ "rules": [ { "name": "x", "enabled": false, "description": "Off.", "update": ["INSERT { GRAPH <{scratch}> { ?s ?p ?o } } WHERE {}"] } ] }""");
        none.UpdateText().Should().BeEmpty();
        new Inference(none, Inference.Default.Answer).RulesFor(ManifestId.Of("P", "m")).Should().StartWith("DROP SILENT GRAPH").And.NotContain(";", "nothing runs after the drop");
    }

    [Theory]
    [InlineData("""{ "rules": [ { "enabled": true, "description": "d", "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"] } ] }""", "has no name")]
    [InlineData("""{ "rules": [ { "name": "a", "description": "d", "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"] } ] }""", "enabled")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": true, "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"] } ] }""", "has no description")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": true, "description": "d" } ] }""", "has no update")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": true, "description": "d", "update": ["INSERT { GRAPH <g> {} } WHERE {}"] } ] }""", "{scratch}")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": true, "description": "d", "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"] }, { "name": "a", "enabled": false, "description": "d", "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"] } ] }""", "listed twice")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": true, "description": "d", "update": ["INSERT { GRAPH <{scratch}> {} } WHERE {}"], "enbled": true } ] }""", "not valid")]
    [InlineData("""{ "rules": [ { "name": "a", "enabled": "yes", "description": "d", "update": [] } ] }""", "not valid")]
    public void A_mistake_in_the_file_is_an_error_when_it_is_read(string json, string message)
    {
        var act = () => InferenceRules.Parse(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{message}*");
    }
}
