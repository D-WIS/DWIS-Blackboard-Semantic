using DWIS.Blackboard.Contracts.Reports;
using VDS.RDF;

namespace DWIS.Semantic.Validation;

/// <summary>
/// Appendix C.6 step 3: the disjointness check and the SHACL shapes run once more on the asserted graph
/// plus the inferred graph, after the final reasoning. Only what is new relative to the pre-processing
/// findings is reported, at stage <see cref="Stage.Commit"/>; a new error downgrades the manifest to
/// core-only (R-SEM-5). The policy applies as in the pass (decisions D-45, D-46), so the same rule has
/// the same level whether it fires before or after reasoning.
/// </summary>
public static class ReasoningCheck
{
    /// <summary>
    /// Runs the two checks on asserted plus inferred and returns the findings that the pre-processing pass
    /// did not already report (same code, source, focus, path and value), with their stage set to the commit.
    /// </summary>
    public static IReadOnlyList<Finding> Run(IGraph asserted, IGraph inferred, VocabularyModel vocabulary, ValidationPolicy policy, IEnumerable<Finding> alreadyReported)
    {
        ArgumentNullException.ThrowIfNull(asserted);
        ArgumentNullException.ThrowIfNull(inferred);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(alreadyReported);

        var reasoned = new Graph();
        reasoned.Merge(asserted);
        reasoned.Merge(inferred);

        var findings = new List<Finding>();
        if (!policy.IsOff(FindingCodes.DisjointClasses))
        {
            PreprocessingPass.CheckDisjointness(PreprocessingPass.AssertedTypes(reasoned), vocabulary, findings);
        }

        if (vocabulary.Shapes is not null && !policy.IsOff(FindingCodes.Shacl))
        {
            findings.AddRange(ShaclCheck.Run(reasoned, vocabulary));
        }

        var known = new HashSet<string>(alreadyReported.Select(Key), StringComparer.Ordinal);
        return policy.Apply(findings)
            .Where(f => !known.Contains(Key(f)))
            .Select(f => f with { Stage = Stage.Commit })
            .OrderByDescending(f => f.Severity)
            .ToList();
    }

    private static string Key(Finding f) => string.Join("\u001f", f.Code, f.Source, f.Focus, f.Path, f.Value);
}
