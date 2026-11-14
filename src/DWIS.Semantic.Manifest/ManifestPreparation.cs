using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Manifests;

namespace DWIS.Semantic.Manifest;

/// <summary>The outcome of <see cref="ManifestPreparation.Prepare"/>.</summary>
/// <param name="Completed">The manifest with validity flags added and injected variables removed. Names and namespaces are as the provider wrote them; translation happens in the graph.</param>
/// <param name="AddedValidityFlags">The variable ids that received a validity flag.</param>
/// <param name="Notes">What was dropped or guessed.</param>
public sealed record PreparedManifest(
    Blackboard.Contracts.Manifests.Manifest Completed,
    IReadOnlyList<string> AddedValidityFlags,
    IReadOnlyList<ManifestNote> Notes);

/// <summary>
/// Appendix C.2: completes the validity flags and drops injected variables. Pure: the same manifest
/// gives the same result, and a manifest that already has its flags comes back unchanged.
/// </summary>
public static class ManifestPreparation
{
    /// <summary>Note code: an injected variable was dropped (decision D-26, R-BC-3).</summary>
    public const string InjectedVariableDropped = "injected-variable-dropped";

    /// <summary>Note code: a validity flag was added for a variable (decision D-23).</summary>
    public const string ValidityFlagAdded = "validity-flag-added";

    /// <summary>Completes the flags and drops injected variables.</summary>
    public static PreparedManifest Prepare(Blackboard.Contracts.Manifests.Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var notes = new List<ManifestNote>();
        foreach (var dropped in manifest.InjectedVariables)
        {
            notes.Add(new ManifestNote(NoteSeverity.Warning, InjectedVariableDropped,
                $"Injected variable '{dropped.InjectedName}' is not supported by the Rig Edition and was dropped.", dropped.InjectedName));
        }

        var alias = manifest.InjectionInformation.ProvidedVariablesNamespaceAlias;
        var subjectsWithFlag = new HashSet<string>(StringComparer.Ordinal);
        var flagsThemselves = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in manifest.InjectedReferences)
        {
            if (Namespaces.ToCanonical(reference.VerbURI) != Vocabulary.IsValidBy)
            {
                continue;
            }

            if (reference.Subject.NameSpace == alias)
            {
                subjectsWithFlag.Add(reference.Subject.ID);
            }

            if (reference.Object.NameSpace == alias)
            {
                flagsThemselves.Add(reference.Object.ID);
            }
        }

        var variables = new List<ProvidedVariable>(manifest.ProvidedVariables);
        var references = new List<InjectedReference>(manifest.InjectedReferences);
        var added = new List<string>();
        foreach (var variable in manifest.ProvidedVariables)
        {
            if (subjectsWithFlag.Contains(variable.VariableID) || flagsThemselves.Contains(variable.VariableID))
            {
                continue;
            }

            var flagId = ManifestIris.ValidityFlagId(variable.VariableID);
            variables.Add(new ProvidedVariable { VariableID = flagId, DataType = "bool", InitialValue = "true", Rank = 0, Dimensions = null });
            references.Add(new InjectedReference
            {
                Subject = new NodeIdentifier { NameSpace = alias, ID = variable.VariableID },
                VerbURI = Namespaces.ToLegacy(Vocabulary.IsValidBy),
                Object = new NodeIdentifier { NameSpace = alias, ID = flagId },
            });
            added.Add(variable.VariableID);
            notes.Add(new ManifestNote(NoteSeverity.Info, ValidityFlagAdded, $"Validity flag '{flagId}' added for variable '{variable.VariableID}'.", variable.VariableID));
        }

        var completed = manifest with
        {
            InjectedVariables = [],
            ProvidedVariables = variables,
            InjectedReferences = references,
        };
        return new PreparedManifest(completed, added, notes);
    }
}
