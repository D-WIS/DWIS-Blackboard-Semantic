using System.Text.Json;
using System.Text.Json.Serialization;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;

namespace DWIS.Semantic.Validation;

/// <summary>The level a rule reports at, or <see cref="Off"/> to drop its findings.</summary>
public enum RuleLevel
{
    /// <summary>The rule does not run, or its findings are dropped.</summary>
    Off,

    /// <summary>Reported; does not affect acceptance.</summary>
    Info,

    /// <summary>Reported; does not affect acceptance.</summary>
    Warning,

    /// <summary>Reported; the manifest goes live core-only.</summary>
    Error,
}

/// <summary>One entry of the policy: the level and why it is set.</summary>
public sealed record RuleSetting
{
    /// <summary>The level the rule reports at.</summary>
    public required RuleLevel Level { get; init; }

    /// <summary>Why the entry exists and what would remove it; free text for the reviewer.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// The validation policy of the pre-processing pass (decision D-45), read from
/// <c>validation-policy.json</c> next to the vocabulary. One entry per rule, keyed by finding code
/// (<c>domain-mismatch</c>, <c>shacl</c>, ...) or by SHACL shape IRI; a rule not listed keeps the level
/// the check or the shape gives it. The catalogue of rules is README.md in this project.
/// </summary>
public sealed record ValidationPolicy
{
    /// <summary>No entry: every finding keeps its designed level.</summary>
    public static ValidationPolicy None { get; } = new();

    /// <summary>The finding codes a policy may name: the pass's own and the manifest step's.</summary>
    public static IReadOnlySet<string> KnownCodes { get; } = new HashSet<string>(
        FindingCodes.All.Concat(
        [
            ManifestPreparation.InjectedVariableDropped,
            ManifestPreparation.ValidityFlagAdded,
            ManifestGraphBuilder.ReferenceToInjectedVariable,
            ManifestGraphBuilder.UnknownNamespaceAlias,
            ManifestGraphBuilder.IncompleteReference,
        ]), StringComparer.Ordinal);

    /// <summary>The entries, by finding code or shape IRI.</summary>
    public IReadOnlyDictionary<string, RuleSetting> Rules { get; init; } = new Dictionary<string, RuleSetting>(StringComparer.Ordinal);

    /// <summary>The level set for a code or shape, or null when the policy does not mention it.</summary>
    public RuleLevel? LevelOf(string codeOrShape) => Rules.TryGetValue(codeOrShape, out var setting) ? setting.Level : null;

    /// <summary>True when the policy switches the code or shape off.</summary>
    public bool IsOff(string codeOrShape) => LevelOf(codeOrShape) == RuleLevel.Off;

    /// <summary>
    /// Applies the policy: an entry for the finding's source (a shape) wins over one for its code; a
    /// finding whose level is off is dropped.
    /// </summary>
    public IReadOnlyList<Finding> Apply(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var result = new List<Finding>();
        foreach (var finding in findings)
        {
            var level = (finding.Source is null ? null : LevelOf(finding.Source)) ?? (finding.Code is null ? null : LevelOf(finding.Code));
            switch (level)
            {
                case null:
                    result.Add(finding);
                    break;
                case RuleLevel.Off:
                    break;
                default:
                    result.Add(finding with { Severity = ToSeverity(level.Value) });
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a policy: <c>{ "rules": { "domain-mismatch": { "level": "warning", "reason": "..." } } }</c>.
    /// Comments and trailing commas are allowed. A key that is neither a known finding code nor an
    /// absolute IRI, an unknown level, or an unknown property is an error, so that a typo is caught when
    /// the file is read and not silently ignored.
    /// </summary>
    public static ValidationPolicy Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PolicyFile file;
        try
        {
            file = JsonSerializer.Deserialize<PolicyFile>(json, JsonOptions) ?? new PolicyFile();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The validation policy is not valid: {e.Message}", e);
        }

        var rules = new Dictionary<string, RuleSetting>(StringComparer.Ordinal);
        foreach (var (key, entry) in file.Rules)
        {
            if (!KnownCodes.Contains(key) && !Uri.IsWellFormedUriString(key, UriKind.Absolute))
            {
                throw new InvalidDataException($"The validation policy names the rule '{key}', which is neither a finding code ({string.Join(", ", KnownCodes.Order(StringComparer.Ordinal))}) nor a shape IRI.");
            }

            if (!Enum.TryParse<RuleLevel>(entry.Level, ignoreCase: true, out var level))
            {
                throw new InvalidDataException($"The validation policy gives the rule '{key}' the level '{entry.Level}'; use error, warning, info or off.");
            }

            rules[key] = new RuleSetting { Level = level, Reason = entry.Reason };
        }

        return new ValidationPolicy { Rules = rules };
    }

    /// <summary>Reads a policy file from disk.</summary>
    public static ValidationPolicy Load(string path) => Parse(File.ReadAllText(path));

    private static Severity ToSeverity(RuleLevel level) => level switch
    {
        RuleLevel.Error => Severity.Error,
        RuleLevel.Warning => Severity.Warning,
        _ => Severity.Info,
    };

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class PolicyFile
    {
        public Dictionary<string, RuleEntry> Rules { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class RuleEntry
    {
        public string? Level { get; init; }

        public string? Reason { get; init; }
    }
}
