using DWIS.Blackboard.Contracts;

namespace DWIS.Semantic.Host;

/// <summary>
/// The settings of the semantic service (specification section 8), bound from the configuration sections
/// <c>Store</c> (see <see cref="Store.FusekiOptions"/>), <c>Vocabulary</c>, <c>Validation</c> and <c>Query</c>.
/// Every value has a default in code; the compose stack, the environment (<c>DWIS__Vocabulary__Version</c>)
/// and the command line (<c>--Vocabulary:Version=…</c>) override it.
/// </summary>
public static class SemanticOptions
{
    /// <summary>The configuration section of the store client.</summary>
    public const string StoreSection = "Store";

    /// <summary>The configuration section of the vocabulary.</summary>
    public const string VocabularySection = "Vocabulary";

    /// <summary>The configuration section of the validation.</summary>
    public const string ValidationSection = "Validation";

    /// <summary>The configuration section of the query limits.</summary>
    public const string QuerySection = "Query";

    /// <summary>The configuration section of the staging table.</summary>
    public const string StagingSection = "Staging";

    /// <summary>The sections the service reads, in the order the effective configuration is printed.</summary>
    public static IReadOnlyList<string> Sections { get; } = [StoreSection, VocabularySection, ValidationSection, QuerySection, StagingSection];

    /// <summary>Layer 1 of section 8: the defaults in code, as configuration keys.</summary>
    public static IReadOnlyDictionary<string, string?> Defaults { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        [$"{StoreSection}:BaseUrl"] = "http://localhost:3030/",
        [$"{StoreSection}:LiveDataset"] = "dwis",
        [$"{StoreSection}:QuarantineDataset"] = "dwis-quarantine",
        [$"{StoreSection}:InferenceDataset"] = "dwis-inf",
        [$"{VocabularySection}:Version"] = "2026-10-14",
        [$"{VocabularySection}:RetrySeconds"] = "5",
        [$"{ValidationSection}:PolicyFile"] = "",
        [$"{QuerySection}:TimeoutSeconds"] = "30",
        [$"{QuerySection}:RowLimit"] = "10000",
        [$"{StagingSection}:TimeLimitSeconds"] = "60",
    };
}

/// <summary>Which vocabulary the service checks manifests against (Appendix C.8: the graphs are named after the version).</summary>
public sealed class VocabularyOptions
{
    /// <summary>The version; the vocabulary graph is <c>http://d-wis.org/vocabulary/{version}</c> and the units graph <c>http://d-wis.org/units/{version}</c>.</summary>
    public string Version { get; set; } = "2026-10-14";

    /// <summary>Seconds between two attempts to read the vocabulary from the store at start-up.</summary>
    public int RetrySeconds { get; set; } = 5;

    /// <summary>The vocabulary graph IRI of the configured version.</summary>
    public string Graph => GraphOf(Version);

    /// <summary>The graph of the unit and quantity individuals (decision D-44) of the configured version.</summary>
    public string UnitsGraph => UnitsGraphOf(Version);

    /// <summary>The vocabulary graph IRI of a version.</summary>
    public static string GraphOf(string version) => $"{Namespaces.Canonical}vocabulary/{version}";

    /// <summary>The units graph IRI of a version.</summary>
    public static string UnitsGraphOf(string version) => $"{Namespaces.Canonical}units/{version}";
}

/// <summary>How manifests are validated.</summary>
public sealed class ValidationOptions
{
    /// <summary>The validation policy file (decisions D-45, D-46); empty for no policy, every rule at its designed level.</summary>
    public string PolicyFile { get; set; } = "";
}

/// <summary>The staging table of the two-phase injection (Appendix C.5).</summary>
public sealed class StagingOptions
{
    /// <summary>Seconds a prepared manifest waits for its commit before the entry is dropped.</summary>
    public int TimeLimitSeconds { get; set; } = 60;
}

/// <summary>The limits of <c>/sparql</c> (R-SEM-8).</summary>
public sealed class QueryLimits
{
    /// <summary>Seconds the store may spend on one query.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Rows (SELECT) or triples (CONSTRUCT, DESCRIBE) kept; the rest is dropped and the answer marked truncated.</summary>
    public int RowLimit { get; set; } = 10_000;

    /// <summary>The limits as the store client takes them.</summary>
    public Store.QueryOptions ToQueryOptions() => new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds), RowLimit = RowLimit };
}
