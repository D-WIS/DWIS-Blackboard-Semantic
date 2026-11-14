namespace DWIS.Semantic.Validation.Tests;

public sealed class VocabularyModelTests : IClassFixture<VocabularyFixture>
{
    private const string Dwis = "http://d-wis.org/";
    private readonly VocabularyModel _model;

    public VocabularyModelTests(VocabularyFixture fixture)
    {
        _model = fixture.Model;
    }

    [Fact]
    public void The_vocabulary_is_indexed_under_the_canonical_namespace()
    {
        _model.Classes.Should().HaveCountGreaterThan(2500).And.Contain(Dwis + "DrillingDataPoint").And.Contain(Dwis + "SPP");
        _model.ObjectProperties.Should().Contain(Dwis + "HasDynamicValue").And.Contain(Dwis + "IsUnitForQuantity");
        _model.DatatypeProperties.Should().Contain(Dwis + "Duration").And.Contain(Dwis + "ProviderName");
        _model.FunctionalProperties.Should().Contain(Dwis + "Duration");
        _model.Classes.Should().NotContain(c => c.StartsWith("http://ddhub.no/", StringComparison.Ordinal));
    }

    [Fact]
    public void Subclass_closure_domains_and_ranges_are_computed()
    {
        _model.SuperClassesOf(Dwis + "SPP").Should().Contain(Dwis + "DrillingDataPoint");
        _model.SuperClassesOf(Dwis + "Bar").Should().Contain(Dwis + "Unit");
        _model.DomainsOf(Dwis + "HasDynamicValue").Should().Contain(Dwis + "DrillingDataPoint", "own domain").And.Contain(Dwis + "DWISNoun", "inherited from the super-property");
        _model.RangesOf(Dwis + "HasDynamicValue").Should().Contain(Dwis + "DynamicDrillingSignal");
        _model.Satisfies([Dwis + "SPP"], Dwis + "DrillingDataPoint").Should().BeTrue();
        _model.Satisfies([Dwis + "Bar"], Dwis + "DrillingDataPoint").Should().BeFalse();
    }

    [Fact]
    public void Union_domains_and_disjoint_classes_are_understood()
    {
        _model.Satisfies([Dwis + "PressureQuantity"], Dwis + "QuantityOrMeasurableQuantity").Should().BeTrue("the named union of the Units definitions");
        _model.DisjointPair([Dwis + "SPP", Dwis + "DynamicDrillingSignal"]).Should().NotBeNull("DrillingDataPoint and DrillingSignal are disjoint");
        _model.DisjointPair([Dwis + "SPP", Dwis + "Measurement"]).Should().BeNull();
        _model.DisjointPair([Dwis + "SPP", Dwis + "ContinuousDataType"]).Should().NotBeNull("the 2026 Noun definitions make DataType and DrillingDataPoint disjoint, which every SmartConnect data point violates");
    }

    [Fact]
    public void The_reference_individuals_have_types()
    {
        _model.TypesOfIndividual(Dwis + "UnitAndQuantity/Bar").Should().Equal(Dwis + "Bar");
        _model.TypesOfIndividual(Dwis + "UnitAndQuantity/PressureDrillingQuantity").Should().Equal(Dwis + "PressureDrillingQuantity");
        _model.TypesOfIndividual(Dwis + "UnitAndQuantity/Nothing").Should().BeNull();
        _model.Shapes.Should().NotBeNull();
    }
}
