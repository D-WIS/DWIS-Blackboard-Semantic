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
    public void A_closed_vocabulary_gives_the_same_domains_and_ranges_as_the_plain_one()
    {
        // The closure (decision D-47) declares every superclass of a domain or range as one too; reduced to the
        // most specific classes, the checks see what the plain vocabulary declares (M5.6).
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocabulary");
        var advanced = Path.Combine(root, "advancedDefinitions");
        var closed = VocabularyModel.Load("closed",
            [Path.Combine(root, "DWISVocabulary.ttl"), Path.Combine(advanced, "DWISVocabularyLogicalDefinitions.Units.ttl"), Path.Combine(root, "closure", "vocabulary.nt")],
            null,
            [Path.Combine(root, "unit-and-quantity-individuals.ttl"), Path.Combine(root, "closure", "units.nt")]);
        closed.Graph.Triples.Count.Should().BeGreaterThan(_model.Graph.Triples.Count);
        foreach (var property in new[] { "HasDynamicValue", "IsAHydraulicRepresentationFor", "IsUnitForQuantity", "IsOfBaseQuantity", "IsLocatedAtEquipment" })
        {
            // The anonymous union range of IsUnitForQuantity is a blank node, named differently in every graph and left out of the closure.
            closed.DomainsOf(Dwis + property).Where(Named).Should().BeEquivalentTo(_model.DomainsOf(Dwis + property).Where(Named), property);
            closed.RangesOf(Dwis + property).Where(Named).Should().BeEquivalentTo(_model.RangesOf(Dwis + property).Where(Named), property);
        }
    }

    private static bool Named(string iri) => !iri.StartsWith("_:", StringComparison.Ordinal);

    [Fact]
    public void Subclass_closure_domains_and_ranges_are_computed()
    {
        _model.SuperClassesOf(Dwis + "SPP").Should().Contain(Dwis + "DrillingDataPoint");
        _model.SuperClassesOf(Dwis + "Bar").Should().Contain(Dwis + "Unit");
        _model.DomainsOf(Dwis + "HasDynamicValue").Should().Contain(Dwis + "DrillingDataPoint", "own domain").And.NotContain(Dwis + "DWISNoun", "inherited from the super-property, but a superclass of the own domain says nothing more");
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
