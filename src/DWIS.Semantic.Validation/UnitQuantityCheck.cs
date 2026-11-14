using System.Globalization;
using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Reports;
using VDS.RDF;

namespace DWIS.Semantic.Validation;

/// <summary>
/// The unit of a signal must have the dimension of the quantity of its data point: the
/// kilogram-for-pressure case of Appendix C.10. Dimensions come from the SI exponent attributes
/// (<c>L</c>, <c>M</c>, <c>T</c>, ...) that the base quantities carry in the reference data; a unit's
/// dimensions are those of the quantities it <c>IsUnitForQuantity</c>, a measurable quantity's that of
/// its <c>IsOfBaseQuantity</c>. When either side has no exponents, nothing is reported.
/// </summary>
public static class UnitQuantityCheck
{
    private static readonly string[] Exponents = ["L", "M", "T", "I", "ThT", "N", "J", "Theta", "Omega"];

    /// <summary>Runs the check on every data point of the manifest.</summary>
    public static IReadOnlyList<Finding> Run(IGraph manifestGraph, VocabularyModel vocabulary)
    {
        ArgumentNullException.ThrowIfNull(manifestGraph);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var reference = vocabulary.ReferenceData;
        if (reference.Triples.Count == 0)
        {
            return [];
        }

        var hasDynamicValue = manifestGraph.CreateUriNode(UriFactory.Create(Namespaces.Canonical + "HasDynamicValue"));
        var isOfMeasurableQuantity = manifestGraph.CreateUriNode(UriFactory.Create(Namespaces.Canonical + "IsOfMeasurableQuantity"));
        var hasUnitOfMeasure = manifestGraph.CreateUriNode(UriFactory.Create(Namespaces.Canonical + "HasUnitOfMeasure"));
        var dimensions = new Dimensions(reference);

        var findings = new List<Finding>();
        foreach (var dataPointToQuantity in manifestGraph.GetTriplesWithPredicate(isOfMeasurableQuantity))
        {
            if (dataPointToQuantity.Object is not IUriNode quantityNode)
            {
                continue;
            }

            var quantityDimension = dimensions.OfQuantity(quantityNode.Uri.AbsoluteUri);
            if (quantityDimension is null)
            {
                continue;
            }

            foreach (var dataPointToSignal in manifestGraph.GetTriplesWithSubjectPredicate(dataPointToQuantity.Subject, hasDynamicValue))
            {
                foreach (var signalToUnit in manifestGraph.GetTriplesWithSubjectPredicate(dataPointToSignal.Object, hasUnitOfMeasure))
                {
                    if (signalToUnit.Object is not IUriNode unitNode)
                    {
                        continue;
                    }

                    var unitDimensions = dimensions.OfUnit(unitNode.Uri.AbsoluteUri);
                    if (unitDimensions.Count == 0 || unitDimensions.Contains(quantityDimension))
                    {
                        continue;
                    }

                    findings.Add(new Finding
                    {
                        Code = FindingCodes.UnitQuantityDimension,
                        Message = $"Unit {PreprocessingPass.Local(unitNode.Uri.AbsoluteUri)} ({Dimensions.Show(unitDimensions.First())}) is not a unit for {PreprocessingPass.Local(quantityNode.Uri.AbsoluteUri)} ({Dimensions.Show(quantityDimension)}).",
                        Focus = PreprocessingPass.Name(signalToUnit.Subject),
                        Path = Namespaces.Canonical + "HasUnitOfMeasure",
                        Value = unitNode.Uri.AbsoluteUri,
                        Source = "unit-quantity-dimension",
                    });
                }
            }
        }

        return findings;
    }

    /// <summary>Dimension vectors of the reference individuals, computed lazily.</summary>
    private sealed class Dimensions
    {
        private readonly IGraph _reference;
        private readonly IUriNode _isUnitForQuantity;
        private readonly IUriNode _isOfBaseQuantity;
        private readonly IUriNode[] _exponents;
        private readonly Dictionary<string, string?> _quantities = new(StringComparer.Ordinal);

        public Dimensions(IGraph reference)
        {
            _reference = reference;
            _isUnitForQuantity = reference.CreateUriNode(UriFactory.Create(Namespaces.Canonical + "IsUnitForQuantity"));
            _isOfBaseQuantity = reference.CreateUriNode(UriFactory.Create(Namespaces.Canonical + "IsOfBaseQuantity"));
            _exponents = Exponents.Select(e => reference.CreateUriNode(UriFactory.Create(Namespaces.Canonical + e))).ToArray();
        }

        /// <summary>"L-1 M1 T-2" for a dimension key.</summary>
        public static string Show(string key) => key;

        /// <summary>The dimension of a quantity, through its base quantity when it has one; null when unknown.</summary>
        public string? OfQuantity(string quantity)
        {
            if (_quantities.TryGetValue(quantity, out var cached))
            {
                return cached;
            }

            var node = _reference.CreateUriNode(UriFactory.Create(quantity));
            var own = Exponent(node);
            if (own is null)
            {
                foreach (var baseQuantity in _reference.GetTriplesWithSubjectPredicate(node, _isOfBaseQuantity))
                {
                    if (baseQuantity.Object is IUriNode b)
                    {
                        own = Exponent(b);
                        if (own is not null)
                        {
                            break;
                        }
                    }
                }
            }

            _quantities[quantity] = own;
            return own;
        }

        /// <summary>The dimensions of every quantity a unit is a unit for.</summary>
        public HashSet<string> OfUnit(string unit)
        {
            var node = _reference.CreateUriNode(UriFactory.Create(unit));
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in _reference.GetTriplesWithSubjectPredicate(node, _isUnitForQuantity))
            {
                if (t.Object is IUriNode q && OfQuantity(q.Uri.AbsoluteUri) is { } d)
                {
                    result.Add(d);
                }
            }

            return result;
        }

        private string? Exponent(INode quantity)
        {
            var parts = new List<string>();
            foreach (var (exponent, name) in _exponents.Zip(Exponents))
            {
                var value = _reference.GetTriplesWithSubjectPredicate(quantity, exponent).Select(t => t.Object).OfType<ILiteralNode>().FirstOrDefault();
                if (value is null)
                {
                    return null;
                }

                if (int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0)
                {
                    parts.Add(name + n.ToString(CultureInfo.InvariantCulture));
                }
            }

            return parts.Count == 0 ? "dimensionless" : string.Join(" ", parts);
        }
    }
}
