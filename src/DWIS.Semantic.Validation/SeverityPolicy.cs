using System.Text.Json;
using System.Text.Json.Serialization;
using DWIS.Blackboard.Contracts.Reports;

namespace DWIS.Semantic.Validation;

/// <summary>
/// Overrides the severity of findings by code or by SHACL shape, as data (R-SEM-4): the project can
/// soften a check or a shape that the current vocabulary definitions make too strict for real
/// manifests, without a code change, and remove the override when the vocabulary project settles it.
/// </summary>
public sealed record SeverityPolicy
{
    /// <summary>No override: every finding keeps the severity the check or the shape gives it.</summary>
    public static SeverityPolicy None { get; } = new();

    /// <summary>Severity by finding code (<c>domain-mismatch</c>, <c>disjoint-classes</c>, ...).</summary>
    public IReadOnlyDictionary<string, Severity> ByCode { get; init; } = new Dictionary<string, Severity>(StringComparer.Ordinal);

    /// <summary>Severity by SHACL shape IRI (the node shape named in <see cref="Finding.Source"/>).</summary>
    public IReadOnlyDictionary<string, Severity> ByShape { get; init; } = new Dictionary<string, Severity>(StringComparer.Ordinal);

    /// <summary>Why each override exists, for the report reader; free text by code or shape.</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Applies the overrides: a shape override wins over a code override.</summary>
    public IReadOnlyList<Finding> Apply(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var result = new List<Finding>();
        foreach (var finding in findings)
        {
            if (finding.Source is not null && ByShape.TryGetValue(finding.Source, out var byShape))
            {
                result.Add(finding with { Severity = byShape });
            }
            else if (finding.Code is not null && ByCode.TryGetValue(finding.Code, out var byCode))
            {
                result.Add(finding with { Severity = byCode });
            }
            else
            {
                result.Add(finding);
            }
        }

        return result;
    }

    /// <summary>Reads a policy file: <c>{ "byCode": { "domain-mismatch": "warning" }, "byShape": { "...": "warning" }, "reasons": { ... } }</c>.</summary>
    public static SeverityPolicy Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<SeverityPolicy>(json, JsonOptions) ?? None;
    }

    /// <summary>Reads a policy file from disk.</summary>
    public static SeverityPolicy Load(string path) => Parse(File.ReadAllText(path));

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
