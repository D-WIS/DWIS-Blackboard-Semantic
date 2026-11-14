namespace DWIS.Semantic.Store;

/// <summary>Where Fuseki is and how its datasets are named (the names of `fuseki/config/dwis.ttl` in DWIS-Blackboard-Tests).</summary>
public sealed record FusekiOptions
{
    /// <summary>The server, without a trailing path. Default: the compose stack on this machine.</summary>
    public Uri BaseUrl { get; init; } = new("http://localhost:3030/");

    /// <summary>The path of the live dataset.</summary>
    public string LiveDataset { get; init; } = "dwis";

    /// <summary>The path of the quarantine dataset.</summary>
    public string QuarantineDataset { get; init; } = "dwis-quarantine";

    /// <summary>The path of the inference view (milestone 2).</summary>
    public string InferenceDataset { get; init; } = "dwis-inf";

    /// <summary>The path for a dataset.</summary>
    public string PathOf(Dataset dataset) => dataset switch
    {
        Dataset.Live => LiveDataset,
        Dataset.Quarantine => QuarantineDataset,
        Dataset.Inference => InferenceDataset,
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, "Unknown dataset."),
    };
}
