using DWIS.Blackboard.Contracts.Reports;
using Xunit;

namespace DWIS.Semantic.Validation.Tests;

/// <summary>The policy file (decision D-45) and the rule catalogue (README.md of the Validation project).</summary>
public sealed class ValidationPolicyTests : IClassFixture<VocabularyFixture>
{
    private readonly VocabularyFixture _fixture;

    public ValidationPolicyTests(VocabularyFixture fixture) => _fixture = fixture;

    [Fact]
    public void Every_finding_code_is_in_the_catalogue()
    {
        var catalogue = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "validation-rules.md"));

        foreach (var code in ValidationPolicy.KnownCodes)
        {
            catalogue.Should().Contain($"`{code}`", $"README.md must have a row for the finding code {code}");
        }
    }

    [Fact]
    public void The_M1_policy_has_one_entry_per_softened_rule_with_a_reason()
    {
        var policy = _fixture.Policy;

        policy.Rules.Keys.Should().BeEquivalentTo(
        [
            FindingCodes.DomainMismatch,
            FindingCodes.RangeMismatch,
            FindingCodes.DisjointClasses,
            "http://d-wis.org/shapes/DrillingSignalShape",
        ]);
        policy.Rules.Values.Should().OnlyContain(r => r.Level == RuleLevel.Warning && !string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void A_shape_entry_wins_over_the_shacl_entry_and_off_drops_the_finding()
    {
        var policy = ValidationPolicy.Parse("""
            {
              "rules": {
                "shacl": { "level": "info" },
                "http://d-wis.org/shapes/A": { "level": "off" },
                "domain-mismatch": { "level": "error", "reason": "strict" }
              }
            }
            """);
        Finding[] findings =
        [
            new() { Code = FindingCodes.Shacl, Source = "http://d-wis.org/shapes/A", Message = "dropped" },
            new() { Code = FindingCodes.Shacl, Source = "http://d-wis.org/shapes/B", Message = "info" },
            new() { Code = FindingCodes.DomainMismatch, Severity = Severity.Warning, Message = "error" },
            new() { Code = FindingCodes.UnknownClass, Message = "untouched" },
        ];

        var applied = policy.Apply(findings);

        applied.Select(f => (f.Message, f.Severity)).Should().Equal(
            ("info", Severity.Info),
            ("error", Severity.Error),
            ("untouched", Severity.Error));
    }

    [Fact]
    public void Off_skips_the_SHACL_and_unit_checks()
    {
        var policy = ValidationPolicy.Parse("""{ "rules": { "shacl": { "level": "off" }, "unit-quantity-dimension": { "level": "off" } } }""");

        var result = Manifests.Run("smartconnect-kilogram.manifest.json", _fixture.Model, new PreprocessingOptions { Policy = policy });

        result.Findings.Should().NotContain(f => f.Code == FindingCodes.Shacl || f.Code == FindingCodes.UnitQuantityDimension);
    }

    [Theory]
    [InlineData("""{ "rules": { "domain-mismtach": { "level": "warning" } } }""", "domain-mismtach")]
    [InlineData("""{ "rules": { "domain-mismatch": { "level": "loud" } } }""", "loud")]
    [InlineData("""{ "rules": { "domain-mismatch": { "severity": "warning" } } }""", "severity")]
    [InlineData("""{ "byCode": { "domain-mismatch": "warning" } }""", "byCode")]
    public void A_typo_in_the_policy_is_an_error_when_the_file_is_read(string json, string culprit)
    {
        var act = () => ValidationPolicy.Parse(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{culprit}*");
    }

    [Fact]
    public void An_empty_policy_changes_nothing()
    {
        var policy = ValidationPolicy.Parse("{}");
        Finding[] findings = [new() { Code = FindingCodes.UnknownClass, Message = "m" }];

        policy.Rules.Should().BeEmpty();
        policy.Apply(findings).Should().BeEquivalentTo(findings);
    }
}
