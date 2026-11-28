using System.Text.Json;
using System.Text.Json.Serialization;

namespace DWIS.Semantic.Ingest;

/// <summary>One reasoning rule of the commit: a name, whether it runs, what it does in words, and the SPARQL update that does it.</summary>
public sealed record InferenceRule
{
    /// <summary>The name the configuration and the log use (<c>sub-class</c>, <c>domain</c>, ...).</summary>
    public required string Name { get; init; }

    /// <summary>The name of the rule in Jena's OWL micro file it mirrors (<c>rdfs9</c>, <c>rdfs2</c>, ...), when it has one.</summary>
    public string? Jena { get; init; }

    /// <summary>True when the commit runs the rule.</summary>
    public required bool Enabled { get; init; }

    /// <summary>What the rule derives, in words, for the reader of the file and of the log.</summary>
    public required string Description { get; init; }

    /// <summary>The SPARQL update (one or more, separated by <c>;</c>) with the placeholders <c>{source}</c> and <c>{scratch}</c>.</summary>
    public required string Update { get; init; }
}

/// <summary>
/// The reasoning rules of the commit (Appendix C.6 step 2; decisions D-64 and D-65), read from
/// <c>inference-rules.json</c>: every rule listed, on or off, with a description, so that the reader of the
/// configuration sees what the Blackboard entails and what it does not. The embedded file is the default;
/// <c>Inference:RulesFile</c> names a replacement.
/// </summary>
public sealed record InferenceRules
{
    /// <summary>The name of the staging graph in a rule.</summary>
    public const string SourcePlaceholder = "{source}";

    /// <summary>The name of the scratch graph in a rule.</summary>
    public const string ScratchPlaceholder = "{scratch}";

    /// <summary>The PREFIX lines every rule may use.</summary>
    public IReadOnlyList<string> Prefixes { get; init; } = [];

    /// <summary>Every rule, in the order the commit runs them.</summary>
    public IReadOnlyList<InferenceRule> Rules { get; init; } = [];

    /// <summary>The rules that run.</summary>
    public IEnumerable<InferenceRule> Enabled => Rules.Where(r => r.Enabled);

    /// <summary>The rules that do not run.</summary>
    public IEnumerable<InferenceRule> Disabled => Rules.Where(r => !r.Enabled);

    /// <summary>The rule of that name, or null.</summary>
    public InferenceRule? this[string name] => Rules.FirstOrDefault(r => r.Name == name);

    /// <summary>
    /// The SPARQL update request of the enabled rules, still with the placeholders: the prefixes, then the
    /// updates in order, separated by <c>;</c>. Empty (no update at all) when no rule is enabled.
    /// </summary>
    public string UpdateText()
    {
        var enabled = Enabled.ToList();
        if (enabled.Count == 0)
        {
            return "";
        }

        return string.Join(Environment.NewLine, Prefixes) + Environment.NewLine + Environment.NewLine
            + string.Join(" ;" + Environment.NewLine + Environment.NewLine, enabled.Select(r => $"# {r.Name}" + Environment.NewLine + r.Update));
    }

    /// <summary>
    /// Reads the rules: <c>{ "prefixes": [...], "rules": [ { "name": "sub-class", "jena": "rdfs9", "enabled": true, "description": "...", "update": ["INSERT ...", "WHERE ..."] } ] }</c>.
    /// Comments and trailing commas are allowed. A missing name, description or update, a missing "enabled",
    /// a repeated name, an unknown property, or an update that does not write to <c>{scratch}</c> is an error,
    /// so that a typo is caught when the file is read and not silently ignored.
    /// </summary>
    public static InferenceRules Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RulesFile file;
        try
        {
            file = JsonSerializer.Deserialize<RulesFile>(json, JsonOptions) ?? new RulesFile();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The inference rules are not valid: {e.Message}", e);
        }

        var rules = new List<InferenceRule>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Rules)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException($"The inference rule number {rules.Count + 1} has no name.");
            }

            if (!names.Add(entry.Name))
            {
                throw new InvalidDataException($"The inference rule '{entry.Name}' is listed twice.");
            }

            if (entry.Enabled is null)
            {
                throw new InvalidDataException($"The inference rule '{entry.Name}' does not say whether it is enabled (\"enabled\": true or false).");
            }

            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                throw new InvalidDataException($"The inference rule '{entry.Name}' has no description.");
            }

            var update = string.Join(Environment.NewLine, entry.Update ?? []).Trim();
            if (update.Length == 0)
            {
                throw new InvalidDataException($"The inference rule '{entry.Name}' has no update.");
            }

            if (!update.Contains(ScratchPlaceholder, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The update of the inference rule '{entry.Name}' does not name the scratch graph as {ScratchPlaceholder}.");
            }

            rules.Add(new InferenceRule { Name = entry.Name, Jena = entry.Jena, Enabled = entry.Enabled.Value, Description = entry.Description, Update = update });
        }

        return new InferenceRules { Prefixes = file.Prefixes, Rules = rules };
    }

    /// <summary>Reads a rules file from disk.</summary>
    public static InferenceRules Load(string path) => Parse(File.ReadAllText(path));

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class RulesFile
    {
        public List<string> Prefixes { get; init; } = [];

        public List<RuleEntry> Rules { get; init; } = [];
    }

    private sealed class RuleEntry
    {
        public string? Name { get; init; }

        public string? Jena { get; init; }

        public bool? Enabled { get; init; }

        public string? Description { get; init; }

        public List<string>? Update { get; init; }
    }
}
