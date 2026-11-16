using System.Text;
using DWIS.Blackboard.Contracts;

namespace DWIS.Semantic.Ingest;

/// <summary>
/// The id of a manifest, <c>{provider}/{name}</c> (Appendix C.5), and the graphs it owns (Appendix C.8).
/// The name is the manifest name made safe for a path: lower case, runs of anything but letters and
/// digits replaced by one hyphen, so "SmartConnect rig signals" gives <c>SmartConnect/smartconnect-rig-signals</c>.
/// </summary>
public sealed record ManifestId
{
    /// <summary>Where the manifest graphs live: <c>http://d-wis.org/manifests/</c>.</summary>
    public const string GraphPrefix = Namespaces.Canonical + "manifests/";

    private ManifestId(string provider, string name)
    {
        Provider = provider;
        Name = name;
    }

    /// <summary>The provider name as written in the manifest.</summary>
    public string Provider { get; }

    /// <summary>The manifest name made safe for a path.</summary>
    public string Name { get; }

    /// <summary>The id, <c>{provider}/{name}</c>.</summary>
    public string Value => $"{Provider}/{Name}";

    /// <summary>The asserted graph, also the subject of the provenance.</summary>
    public string AssertedGraph => GraphPrefix + Value;

    /// <summary>The inferred graph (milestone 2).</summary>
    public string InferredGraph => AssertedGraph + "/inferred";

    /// <summary>The provenance graph.</summary>
    public string ProvenanceGraph => AssertedGraph + "/provenance";

    /// <summary>The quarantine graph, in the quarantine dataset.</summary>
    public string QuarantineGraph => AssertedGraph + "/quarantine";

    /// <summary>The staging graph: the new asserted triples during a commit, for the inference view to reason over before the manifest's graphs are replaced in one update.</summary>
    public string StagingGraph => AssertedGraph + "/staging";

    /// <summary>The id of a manifest.</summary>
    public static ManifestId Of(Blackboard.Contracts.Manifests.Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Of(manifest.Provider.Name, manifest.ManifestName);
    }

    /// <summary>The id from a provider name and a manifest name (the name is made safe here).</summary>
    public static ManifestId Of(string provider, string manifestName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (provider.Contains('/', StringComparison.Ordinal) || provider.Trim() != provider)
        {
            throw new ArgumentException("A provider name has no slash and no surrounding blanks.", nameof(provider));
        }

        var name = Slug(manifestName);
        if (name.Length == 0)
        {
            throw new ArgumentException("A manifest name needs at least one letter or digit.", nameof(manifestName));
        }

        return new ManifestId(provider, name);
    }

    /// <summary>Reads an id back from its text form, <c>{provider}/{name}</c>; null when the text is not one.</summary>
    public static ManifestId? Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1 || text.IndexOf('/', slash + 1) >= 0)
        {
            return null;
        }

        var provider = text[..slash];
        var name = text[(slash + 1)..];
        return name == Slug(name) && provider.Trim() == provider ? new ManifestId(provider, name) : null;
    }

    /// <summary>Lower case; every run of characters other than ASCII letters and digits becomes one hyphen; no hyphen at either end.</summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = new StringBuilder(text.Length);
        var pendingHyphen = false;
        foreach (var c in text)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(c);
                pendingHyphen = false;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                if (pendingHyphen && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append((char)(c + ('a' - 'A')));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return result.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
