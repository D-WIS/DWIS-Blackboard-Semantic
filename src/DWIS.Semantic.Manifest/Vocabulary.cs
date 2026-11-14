using DWIS.Blackboard.Contracts;

namespace DWIS.Semantic.Manifest;

/// <summary>The few vocabulary terms the conversion itself needs, as canonical IRIs.</summary>
public static class Vocabulary
{
    /// <summary><c>rdf:type</c>.</summary>
    public const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

    /// <summary>The verb that, in a manifest reference, means a class assertion (deprecated form, Appendix C.4).</summary>
    public const string BelongsToClass = Namespaces.Canonical + "BelongsToClass";

    /// <summary>Links a node or a variable to its provider.</summary>
    public const string IsProvidedBy = Namespaces.Canonical + "IsProvidedBy";

    /// <summary>Links a variable to its validity flag.</summary>
    public const string IsValidBy = Namespaces.Canonical + "IsValidBy";

    /// <summary>The class of a provider.</summary>
    public const string DataProvider = Namespaces.Canonical + "DataProvider";

    /// <summary>The name attribute of a provider.</summary>
    public const string ProviderName = Namespaces.Canonical + "ProviderName";

    /// <summary>The class given to every provided variable.</summary>
    public const string DynamicDrillingSignal = Namespaces.Canonical + "DynamicDrillingSignal";
}
