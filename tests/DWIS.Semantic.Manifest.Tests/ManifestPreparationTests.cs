using DWIS.Blackboard.Contracts.Manifests;

namespace DWIS.Semantic.Manifest.Tests;

/// <summary>Appendix C.2: validity flags completed, injected variables dropped (R-RT-4, R-BC-3).</summary>
public sealed class ManifestPreparationTests
{
    [Fact]
    public void Every_SmartConnect_variable_gets_a_validity_flag()
    {
        var prepared = ManifestPreparation.Prepare(Fixture.SmartConnect());

        prepared.AddedValidityFlags.Should().HaveCount(254);
        prepared.Completed.ProvidedVariables.Should().HaveCount(508);
        prepared.Completed.InjectedReferences.Should().HaveCount(1537 + 254);
        prepared.Completed.InjectedNodes.Should().HaveCount(391);
        prepared.Notes.Should().OnlyContain(n => n.Code == ManifestPreparation.ValidityFlagAdded && n.Severity == NoteSeverity.Info);

        var flag = prepared.Completed.ProvidedVariables.Single(v => v.VariableID == "circulationBaseOil10s_volume_validitySignal");
        flag.Should().BeEquivalentTo(new ProvidedVariable { VariableID = "circulationBaseOil10s_volume_validitySignal", DataType = "bool", InitialValue = "true", Rank = 0 });
        prepared.Completed.InjectedReferences.Should().ContainEquivalentOf(new InjectedReference
        {
            Subject = Fixture.Variables("circulationBaseOil10s_volume"),
            VerbURI = "http://ddhub.no/IsValidBy",
            Object = Fixture.Variables("circulationBaseOil10s_volume_validitySignal"),
        });
    }

    [Fact]
    public void A_variable_that_already_has_a_flag_gets_none_and_the_flag_gets_none_either()
    {
        var manifest = Fixture.Small(new InjectedReference { Subject = Fixture.Variables("spp"), VerbURI = "http://d-wis.org/IsValidBy", Object = Fixture.Variables("sppOk") }) with
        {
            ProvidedVariables = [new ProvidedVariable { VariableID = "spp", DataType = "double" }, new ProvidedVariable { VariableID = "sppOk", DataType = "bool" }],
        };

        var prepared = ManifestPreparation.Prepare(manifest);

        prepared.AddedValidityFlags.Should().BeEmpty();
        prepared.Completed.ProvidedVariables.Should().HaveCount(2);
    }

    [Fact]
    public void Preparing_twice_changes_nothing_the_second_time()
    {
        var once = ManifestPreparation.Prepare(Fixture.SmartConnect());

        var twice = ManifestPreparation.Prepare(once.Completed);

        twice.AddedValidityFlags.Should().BeEmpty();
        twice.Completed.Should().BeEquivalentTo(once.Completed);
    }

    [Fact]
    public void Injected_variables_are_dropped_with_one_warning_each()
    {
        var manifest = Fixture.Small() with
        {
            InjectedVariables = [new InjectedVariable { InjectedName = "mirror1", DataType = "double" }, new InjectedVariable { InjectedName = "mirror2", DataType = "double" }],
        };

        var prepared = ManifestPreparation.Prepare(manifest);

        prepared.Completed.InjectedVariables.Should().BeEmpty();
        prepared.Notes.Where(n => n.Code == ManifestPreparation.InjectedVariableDropped).Select(n => n.Subject).Should().Equal("mirror1", "mirror2");
        prepared.Notes.Where(n => n.Code == ManifestPreparation.InjectedVariableDropped).Should().OnlyContain(n => n.Severity == NoteSeverity.Warning);
    }

    [Fact]
    public void The_original_manifest_is_not_changed()
    {
        var manifest = Fixture.Small();
        var before = ManifestJson.Serialize(manifest);

        ManifestPreparation.Prepare(manifest);

        ManifestJson.Serialize(manifest).Should().Be(before);
    }
}
