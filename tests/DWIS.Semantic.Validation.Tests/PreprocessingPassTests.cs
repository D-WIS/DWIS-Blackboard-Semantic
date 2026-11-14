using DWIS.Blackboard.Contracts.Reports;
using Xunit;

namespace DWIS.Semantic.Validation.Tests;

/// <summary>Appendix C.4 on the SmartConnect fixture and its edited copies (C.10).</summary>
public sealed class PreprocessingPassTests : IClassFixture<VocabularyFixture>
{
    private const string Dwis = "http://d-wis.org/";
    private readonly VocabularyModel _vocabulary;
    private readonly PreprocessingOptions _m1;
    private readonly ITestOutputHelper _output;

    public PreprocessingPassTests(VocabularyFixture fixture, ITestOutputHelper output)
    {
        _vocabulary = fixture.Model;
        _m1 = new PreprocessingOptions { Policy = fixture.Policy };
        _output = output;
    }

    [Fact]
    public void The_SmartConnect_manifest_is_accepted_under_the_M1_policy_and_its_warnings_are_known()
    {
        var result = Manifests.Run("smartconnect.manifest.json", _vocabulary, _m1);
        _output.WriteLine(Manifests.Summary(result));
        _output.WriteLine($"{result.Milliseconds} ms");

        Manifests.Errors(result).Should().BeEmpty(Manifests.Summary(result));
        result.Accepted.Should().BeTrue();
        var warnings = result.Findings.Where(f => f.Severity == Severity.Warning).GroupBy(f => f.Source ?? f.Code).ToDictionary(g => g.Key!, g => g.Count());
        warnings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [FindingCodes.DisjointClasses] = 254,
            [Dwis + "shapes/DrillingSignalShape"] = 303,
            [FindingCodes.RangeMismatch] = 22,
            [FindingCodes.DomainMismatch] = 4,
        }, "every warning is explained in validation-policy.json (Appendix C.10)");
        result.Findings.Should().Contain(f => f.Code == FindingCodes.BelongsToClassUsed && f.Severity == Severity.Info);
        result.Findings.Where(f => f.Code == "validity-flag-added").Should().HaveCount(254);
    }

    [Fact]
    public void Without_the_policy_the_same_manifest_is_core_only()
    {
        var result = Manifests.Run("smartconnect.manifest.json", _vocabulary);

        result.Accepted.Should().BeFalse();
        Manifests.Errors(result).Should().HaveCount(254 + 303 + 22 + 4);
    }

    [Fact]
    public void The_kilogram_copy_has_exactly_one_error()
    {
        var result = Manifests.Run("smartconnect-kilogram.manifest.json", _vocabulary, _m1);
        _output.WriteLine(Manifests.Summary(result));

        var error = Manifests.Errors(result).Should().ContainSingle().Subject;
        error.Code.Should().Be(FindingCodes.UnitQuantityDimension);
        error.Focus.Should().Be(Dwis + "SmartConnect/Variables/SmartConnect.mainwellCirculationCommon_pressureStandPipe");
        error.Path.Should().Be(Dwis + "HasUnitOfMeasure");
        error.Value.Should().Be(Dwis + "UnitAndQuantity/Kilogram");
        error.Message.Should().Contain("Kilogram").And.Contain("PressureDrillingQuantity");
        result.ToReport("SmartConnect/smartconnect", _vocabulary.Version).Live.Should().Be(new LiveParts(true, false));
    }

    [Fact]
    public void The_dangling_copy_has_exactly_one_error()
    {
        var result = Manifests.Run("smartconnect-dangling.manifest.json", _vocabulary, _m1);

        var error = Manifests.Errors(result).Should().ContainSingle().Subject;
        error.Code.Should().Be(FindingCodes.DanglingReference);
        error.Focus.Should().Be(Dwis + "SmartConnect/mainwellCirculationCommon_pressureStandPipe#01");
        error.Value.Should().Be(Dwis + "SmartConnect/standpipeThatDoesNotExist");
    }

    [Fact]
    public void A_reference_into_the_store_is_not_dangling_when_the_store_knows_it()
    {
        var options = new PreprocessingOptions
        {
            Policy = _m1.Policy,
            ExistsInStore = iri => iri == Dwis + "SmartConnect/standpipeThatDoesNotExist",
            TypesInStore = iri => iri == Dwis + "SmartConnect/standpipeThatDoesNotExist" ? new HashSet<string> { Dwis + "MudStandpipe" } : null,
        };

        var result = Manifests.Run("smartconnect-dangling.manifest.json", _vocabulary, options);

        Manifests.Errors(result).Should().BeEmpty();
    }

    [Fact]
    public void The_unknown_class_copy_has_exactly_one_error()
    {
        var result = Manifests.Run("smartconnect-unknown-class.manifest.json", _vocabulary, _m1);

        var error = Manifests.Errors(result).Should().ContainSingle().Subject;
        error.Code.Should().Be(FindingCodes.UnknownClass);
        error.Focus.Should().Be(Dwis + "SmartConnect/mainwellCirculationCommon_pressureStandPipe#01");
        error.Value.Should().Be(Dwis + "MeasurementOfTheFuture");
    }

    [Fact]
    public void The_injected_variables_copy_is_accepted_with_a_warning()
    {
        var result = Manifests.Run("smartconnect-injected-variables.manifest.json", _vocabulary, _m1);

        result.Accepted.Should().BeTrue();
        result.Findings.Should().ContainSingle(f => f.Code == "injected-variable-dropped" && f.Severity == Severity.Warning && f.Focus == "mirror1");
    }

    [Fact]
    public void Domain_range_functional_and_disjoint_checks_fire_on_a_small_manifest()
    {
        var manifest = new Blackboard.Contracts.Manifests.Manifest
        {
            Provider = new Blackboard.Contracts.Manifests.Provider { Name = "Rig" },
            ProvidedVariables = [new Blackboard.Contracts.Manifests.ProvidedVariable { VariableID = "spp", DataType = "double" }],
            InjectedNodes =
            [
                new Blackboard.Contracts.Manifests.InjectedNode { UniqueName = "spp#01", TypeDictionaryURI = "SPP" },
                new Blackboard.Contracts.Manifests.InjectedNode { UniqueName = "window", TypeDictionaryURI = "TimeWindow", Fields = [new Blackboard.Contracts.Manifests.Field { FieldName = "Duration", FieldValue = "1" }, new Blackboard.Contracts.Manifests.Field { FieldName = "Duration", FieldValue = "2" }] },
            ],
            InjectedReferences =
            [
                // range mismatch: HasDynamicValue expects a DynamicDrillingSignal, gets a node
                Reference("Nodes", "spp#01", "HasDynamicValue", "Nodes", "window"),
                // domain mismatch: the variable (a DynamicDrillingSignal) is not a DrillingDataPoint
                Reference("ProvidedVariables", "spp", "IsOfMeasurableQuantity", "http://ddhub.no/UnitAndQuantity/", "PressureDrillingQuantity"),
                // disjoint: the data point is also asserted to be a signal
                Reference("Nodes", "spp#01", "BelongsToClass", "http://ddhub.no/", "http://ddhub.no/DynamicDrillingSignal"),
                // unknown verb
                Reference("Nodes", "spp#01", "IsMeasuredWith", "Nodes", "window"),
            ],
        };
        var prepared = Manifest.ManifestPreparation.Prepare(manifest);
        var result = PreprocessingPass.Run(prepared, Manifest.ManifestGraphBuilder.Build(prepared), _vocabulary, new PreprocessingOptions { Policy = ValidationPolicy.Parse("""{ "rules": { "shacl": { "level": "off" } } }""") });
        _output.WriteLine(Manifests.Summary(result));

        var codes = Manifests.Errors(result).Select(f => f.Code).ToList();
        codes.Should().Contain(FindingCodes.RangeMismatch);
        codes.Should().Contain(FindingCodes.DomainMismatch);
        codes.Should().Contain(FindingCodes.DisjointClasses);
        codes.Should().Contain(FindingCodes.FunctionalConflict);
        codes.Should().Contain(FindingCodes.UnknownVerb);
    }

    [Fact]
    public void A_shape_violation_is_reported_with_its_focus_path_and_source()
    {
        // A data point whose quantity is not a MeasurableQuantity: DrillingDataPoint shapes and the
        // IsOfMeasurableQuantity range both object.
        var manifest = new Blackboard.Contracts.Manifests.Manifest
        {
            Provider = new Blackboard.Contracts.Manifests.Provider { Name = "Rig" },
            InjectedNodes = [new Blackboard.Contracts.Manifests.InjectedNode { UniqueName = "mq", TypeDictionaryURI = "MeasurableQuantity" }],
        };
        var prepared = Manifest.ManifestPreparation.Prepare(manifest);
        var result = PreprocessingPass.Run(prepared, Manifest.ManifestGraphBuilder.Build(prepared), _vocabulary);
        _output.WriteLine(Manifests.Summary(result));

        var shacl = result.Findings.Should().ContainSingle(f => f.Code == FindingCodes.Shacl, Manifests.Summary(result)).Subject;
        shacl.Severity.Should().Be(Severity.Error);
        shacl.Focus.Should().Be(Dwis + "Rig/mq");
        shacl.Path.Should().Be(Dwis + "IsOfBaseQuantity");
        shacl.Source.Should().Be(Dwis + "shapes/MeasurableQuantityShape");
        shacl.Message.Should().Contain("base Quantity");
    }

    private static Blackboard.Contracts.Manifests.InjectedReference Reference(string sns, string sid, string verb, string ons, string oid) => new()
    {
        Subject = new Blackboard.Contracts.Manifests.NodeIdentifier { NameSpace = sns, ID = sid },
        VerbURI = "http://ddhub.no/" + verb,
        Object = new Blackboard.Contracts.Manifests.NodeIdentifier { NameSpace = ons, ID = oid },
    };
}
