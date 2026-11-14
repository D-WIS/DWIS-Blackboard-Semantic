using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;

namespace DWIS.Semantic.Validation.Tests;

/// <summary>
/// The vocabulary of 14 October 2026 with the advanced definitions, the SHACL shapes and the unit and
/// quantity individuals (copies of DWIS-Blackboard-Tests/fixtures), loaded once for all tests.
/// </summary>
public sealed class VocabularyFixture
{
    public VocabularyFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary");
        var advanced = Path.Combine(root, "advancedDefinitions");
        Model = VocabularyModel.Load(
            "2026-10-14",
            [
                Path.Combine(root, "DWISVocabulary.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Noun.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Prototypes.ttl"),
                Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Units.ttl"),
            ],
            [Path.Combine(advanced, "DWISVocabularyShapes.ttl")],
            [Path.Combine(root, "unit-and-quantity-individuals.ttl")]);
    }

    public VocabularyModel Model { get; }

    /// <summary>The milestone 1 validation policy of the Tests repository (copy).</summary>
    public ValidationPolicy Policy { get; } = ValidationPolicy.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary", "validation-policy.json"));
}

internal static class Manifests
{
    public static Blackboard.Contracts.Manifests.Manifest Load(string name) =>
        ManifestJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifests", name)));

    public static PreprocessingResult Run(string name, VocabularyModel vocabulary, PreprocessingOptions? options = null)
    {
        var prepared = ManifestPreparation.Prepare(Load(name));
        var graph = ManifestGraphBuilder.Build(prepared);
        return PreprocessingPass.Run(prepared, graph, vocabulary, options);
    }

    public static string Summary(PreprocessingResult result)
    {
        var text = string.Join("\n", result.Findings.GroupBy(f => (f.Severity, f.Code, f.Source)).OrderByDescending(g => g.Key.Severity)
            .Select(g => $"{g.Key.Severity} {g.Key.Code} {g.Key.Source} x{g.Count()}: {g.First().Message}"));
        Console.WriteLine(text);
        return text;
    }

    public static IEnumerable<Finding> Errors(PreprocessingResult result) => result.Findings.Where(f => f.Severity == Severity.Error);
}
