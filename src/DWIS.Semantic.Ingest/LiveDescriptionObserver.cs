namespace DWIS.Semantic.Ingest;

/// <summary>What happened to the live description (specification 3.5: every such change starts a round of the registered queries).</summary>
public enum DescriptionChangeKind
{
    /// <summary>A manifest went live (injected or updated).</summary>
    Committed,

    /// <summary>A manifest was removed.</summary>
    Removed,

    /// <summary>Every live manifest was re-derived against a new vocabulary.</summary>
    Rederived,
}

/// <summary>A change of the live description and the manifests it touched.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Manifests">The manifests touched, in the order they were written.</param>
public sealed record DescriptionChange(DescriptionChangeKind Kind, IReadOnlyList<ManifestId> Manifests);

/// <summary>
/// Told by the pipeline after every change to the live description: a commit, a remove, a re-derive (once, at
/// its end). The registered queries are re-evaluated here (milestone 3); an observer that throws is logged
/// and never fails the change it was told about.
/// </summary>
public interface ILiveDescriptionObserver
{
    /// <summary>Called after the change is written.</summary>
    Task ChangedAsync(DescriptionChange change, CancellationToken cancellationToken);
}
