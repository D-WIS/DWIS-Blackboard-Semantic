using DWIS.Blackboard.Contracts;

namespace DWIS.Semantic.Manifest;

/// <summary>
/// The IRI scheme of Appendix C.3, the same as today's server so that injection results and query
/// results keep their shape (after translation back to the legacy namespace on the OPC UA side).
/// Every IRI is canonical (<see cref="Namespaces.Canonical"/>).
/// </summary>
public static class ManifestIris
{
    /// <summary>Suffix of the provided-variables namespace of a provider.</summary>
    public const string VariablesSegment = "Variables";

    /// <summary>Suffix of the injected-vocabulary namespace of a provider.</summary>
    public const string SemanticSegment = "Semantic";

    /// <summary>The validity flag of a variable is the variable id plus this suffix (Appendix C.2).</summary>
    public const string ValiditySuffix = "_validitySignal";

    /// <summary><c>http://d-wis.org/{provider}/{provider}</c>: the provider individual.</summary>
    public static string Provider(string provider) => $"{Namespaces.Canonical}{provider}/{provider}";

    /// <summary><c>http://d-wis.org/{provider}/{uniqueName}</c>: an injected node.</summary>
    public static string Node(string provider, string uniqueName) => $"{Namespaces.Canonical}{provider}/{uniqueName}";

    /// <summary><c>http://d-wis.org/{provider}/Variables/{provider}.{variableId}</c>: a provided variable.</summary>
    public static string ProvidedVariable(string provider, string variableId) => $"{Namespaces.Canonical}{provider}/{VariablesSegment}/{provider}.{variableId}";

    /// <summary><c>http://d-wis.org/{provider}/Semantic/{name}</c>: a term of a vocabulary carried by the manifest.</summary>
    public static string InjectedTerm(string provider, string name) => $"{Namespaces.Canonical}{provider}/{SemanticSegment}/{name}";

    /// <summary>A vocabulary term: a bare name becomes <c>http://d-wis.org/{name}</c>; an IRI is translated to the canonical namespace.</summary>
    public static string Term(string nameOrIri)
    {
        ArgumentNullException.ThrowIfNull(nameOrIri);
        return IsAbsolute(nameOrIri) ? Namespaces.ToCanonical(nameOrIri) : Namespaces.Canonical + nameOrIri;
    }

    /// <summary>The validity flag id of a variable id.</summary>
    public static string ValidityFlagId(string variableId) => variableId + ValiditySuffix;

    /// <summary>True when the text looks like an absolute IRI (has a scheme).</summary>
    public static bool IsAbsolute(string text) => text.Contains("://", StringComparison.Ordinal) || text.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);
}
