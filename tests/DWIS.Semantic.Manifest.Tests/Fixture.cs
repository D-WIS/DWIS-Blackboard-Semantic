using DWIS.Blackboard.Contracts.Manifests;

namespace DWIS.Semantic.Manifest.Tests;

internal static class Fixture
{
    // Copy of DWIS-Blackboard-Tests/fixtures/manifests/smartconnect.manifest.json (DWIS-SmartConnect d559ea6):
    // 254 provided variables, 391 nodes, 1537 references, no IsValidBy, no injected variable.
    public static Blackboard.Contracts.Manifests.Manifest SmartConnect() =>
        ManifestJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "smartconnect.manifest.json")));

    /// <summary>A small manifest in the shape a provider writes it.</summary>
    public static Blackboard.Contracts.Manifests.Manifest Small(params InjectedReference[] extraReferences) => new()
    {
        ManifestName = "small",
        Provider = new Provider { Name = "Rig", Company = "Test" },
        ProvidedVariables = [new ProvidedVariable { VariableID = "spp", DataType = "double" }],
        InjectedNodes =
        [
            new InjectedNode { UniqueName = "spp#01", TypeDictionaryURI = "Measurement" },
            new InjectedNode { UniqueName = "window", TypeDictionaryURI = "TimeWindow", Fields = [new Field { FieldName = "Duration", FieldValue = "180.0" }] },
        ],
        InjectedReferences =
        [
            new InjectedReference { Subject = Nodes("spp#01"), VerbURI = "http://ddhub.no/BelongsToClass", Object = Root("http://ddhub.no/SPP") },
            new InjectedReference { Subject = Nodes("spp#01"), VerbURI = "http://ddhub.no/HasDynamicValue", Object = Variables("spp") },
            new InjectedReference { Subject = Nodes("spp#01"), VerbURI = "http://ddhub.no/IsOfMeasurableQuantity", Object = Units("PressureDrillingQuantity") },
            .. extraReferences,
        ],
    };

    public static NodeIdentifier Nodes(string id) => new() { NameSpace = "Nodes", ID = id };

    public static NodeIdentifier Variables(string id) => new() { NameSpace = "ProvidedVariables", ID = id };

    public static NodeIdentifier Root(string iri) => new() { NameSpace = "http://ddhub.no/", ID = iri };

    public static NodeIdentifier Units(string id) => new() { NameSpace = "http://ddhub.no/UnitAndQuantity/", ID = id };
}
