using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Semantic.Validation;

namespace DWIS.Semantic.Ingest.Tests;

/// <summary>The vocabulary of 14 October 2026 with shapes and units, and the milestone 1 policy, loaded once (copies of DWIS-Blackboard-Tests/fixtures).</summary>
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
        Policy = ValidationPolicy.Load(Path.Combine(root, "validation-policy.json"));
    }

    public VocabularyModel Model { get; }

    public ValidationPolicy Policy { get; }

    public static Blackboard.Contracts.Manifests.Manifest Manifest(string name) =>
        ManifestJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifests", name)));
}
