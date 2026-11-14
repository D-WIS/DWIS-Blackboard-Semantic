namespace DWIS.Semantic.Validation;

/// <summary>The stable codes of the findings the pre-processing pass produces (Appendix C.4).</summary>
public static class FindingCodes
{
    /// <summary>A node is typed by a class the vocabulary does not have.</summary>
    public const string UnknownClass = "unknown-class";

    /// <summary>A reference uses a verb the vocabulary does not have.</summary>
    public const string UnknownVerb = "unknown-verb";

    /// <summary>A field uses an attribute the vocabulary does not have.</summary>
    public const string UnknownAttribute = "unknown-attribute";

    /// <summary>The subject of a verb or attribute is not of its domain.</summary>
    public const string DomainMismatch = "domain-mismatch";

    /// <summary>The object of a verb is not of its range.</summary>
    public const string RangeMismatch = "range-mismatch";

    /// <summary>A functional verb or attribute has two values on one subject.</summary>
    public const string FunctionalConflict = "functional-conflict";

    /// <summary>A reference names a node that exists neither in the manifest nor in the store.</summary>
    public const string DanglingReference = "dangling-reference";

    /// <summary>A node is asserted to be of two disjoint classes.</summary>
    public const string DisjointClasses = "disjoint-classes";

    /// <summary>A SHACL shape reported a result; the severity is the one the shape declares.</summary>
    public const string Shacl = "shacl";

    /// <summary>The unit of a signal has a dimension that does not match the quantity of its data point.</summary>
    public const string UnitQuantityDimension = "unit-quantity-dimension";

    /// <summary>The manifest uses BelongsToClass, the deprecated form of a class assertion.</summary>
    public const string BelongsToClassUsed = "belongs-to-class-used";
}
