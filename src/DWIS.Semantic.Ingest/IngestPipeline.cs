using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;
using DWIS.Semantic.Store;
using DWIS.Semantic.Validation;
using VDS.RDF;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;

namespace DWIS.Semantic.Ingest;

/// <summary>A manifest that went through the pure part of the pipeline (C.2 to C.5) and waits for its commit.</summary>
/// <param name="Id">The manifest id.</param>
/// <param name="ManifestName">The manifest name as written.</param>
/// <param name="ContentHash">SHA-256 of the manifest JSON in its canonical form.</param>
/// <param name="ManifestJson">The manifest in its canonical JSON form, kept in the provenance.</param>
/// <param name="Graph">The asserted graph.</param>
/// <param name="Split">Its core and its rest.</param>
/// <param name="Report">The report of the pre-processing pass; the commit adds the statistics.</param>
/// <param name="InferencePredicates">The predicates the commit asks the inference view for (<see cref="Inference.PredicatesOf"/>).</param>
/// <param name="Vocabulary">The vocabulary the manifest was checked against; the re-check after reasoning uses it again.</param>
/// <param name="Policy">The policy in force.</param>
public sealed record StagedManifest(
    ManifestId Id,
    string ManifestName,
    string ContentHash,
    string ManifestJson,
    ManifestGraph Graph,
    SplitGraph Split,
    IngestionReport Report,
    IReadOnlyList<string> InferencePredicates,
    VocabularyModel Vocabulary,
    ValidationPolicy Policy)
{
    /// <summary>True when the whole manifest may go live.</summary>
    public bool Accepted => Report.Accepted;
}

/// <summary>
/// The injection pipeline of specification 4 and Appendix C: prepare (C.2 to C.5, pure) and commit
/// (C.6 steps 1 to 4: the asserted graph, then the inferred graph read from the inference view, then the
/// re-check on both with a downgrade to core-only when it finds a new error, then the provenance; the
/// quarantine graph when rejected). The endpoints and the registered queries come with M3 and M4.
/// Injecting the same manifest twice returns the stored report without re-processing (C.5).
/// </summary>
public sealed class IngestPipeline
{
    private readonly ITripleStore _store;
    private readonly TimeProvider _clock;

    /// <summary>Creates the pipeline over a store.</summary>
    public IngestPipeline(ITripleStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The canonical JSON of a manifest and its hash, <c>sha256:</c> and 64 hex digits.</summary>
    public static (string Json, string Hash) Canonical(Blackboard.Contracts.Manifests.Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = ManifestJson.Serialize(manifest);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return (json, "sha256:" + hash);
    }

    /// <summary>
    /// The pure part: names and validity flags (C.2), triples (C.3), the pre-processing pass (C.4) and the
    /// split (C.5). Same manifest, same vocabulary, same policy: same result. Nothing is written.
    /// </summary>
    public static StagedManifest Prepare(Blackboard.Contracts.Manifests.Manifest manifest, VocabularyModel vocabulary, ValidationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var id = ManifestId.Of(manifest);
        var (json, hash) = Canonical(manifest);

        var prepared = ManifestPreparation.Prepare(manifest);
        var graph = ManifestGraphBuilder.Build(prepared);
        var pass = PreprocessingPass.Run(prepared, graph, vocabulary, new PreprocessingOptions { Policy = policy ?? ValidationPolicy.None });
        var split = CoreSplit.Split(graph);
        var report = pass.ToReport(id.Value, vocabulary.Version);
        return new StagedManifest(id, manifest.ManifestName, hash, json, graph, split, report, Inference.PredicatesOf(vocabulary), vocabulary, policy ?? ValidationPolicy.None);
    }

    /// <summary>
    /// Writes the staged manifest to the live dataset: the asserted graph (whole when accepted, core only
    /// when not) after dropping what a previous injection of the same id left (C.7); the inferred graph,
    /// read from the inference view once the asserted triples are visible there; the re-check of the
    /// disjointness and the shapes on both graphs, which downgrades the manifest to core-only when it
    /// finds a new error (the asserted graph is rewritten as the core and the inferred graph recomputed);
    /// then the provenance and the commit time. The whole manifest graph goes to the quarantine dataset
    /// when the manifest is not accepted, else no quarantine graph.
    /// </summary>
    public async Task<IngestionReport> CommitAsync(StagedManifest staged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var watch = Stopwatch.StartNew();
        var id = staged.Id;
        var accepted = staged.Accepted;
        var asserted = accepted ? staged.Graph.Graph : staged.Split.Core;

        // Step 1: the asserted graph, in place of whatever the previous injection of this id left.
        await _store.UpdateAsync(Dataset.Live, AssertedUpdate(id, asserted, dropFirst: true), cancellationToken).ConfigureAwait(false);

        // Step 2: what the reasoner adds about this provider's resources, now that the asserted triples are
        // in the union the view reasons over (C.6 step 2; the view stores nothing, the graph here does).
        var inferred = await Inference.ConsequencesAsync(_store, Inference.SubjectsOf(asserted), staged.InferencePredicates, cancellationToken).ConfigureAwait(false);

        // Step 3: the re-check on asserted plus inferred. A new error downgrades the manifest to core-only:
        // the core replaces the whole graph and the inferred graph is recomputed for it (R-SEM-5).
        var findings = staged.Report.Findings.ToList();
        var afterReasoning = ReasoningCheck.Run(asserted, inferred, staged.Vocabulary, staged.Policy, findings);
        findings.AddRange(afterReasoning);
        if (accepted && afterReasoning.Any(f => f.Severity == Severity.Error))
        {
            accepted = false;
            asserted = staged.Split.Core;
            await _store.UpdateAsync(Dataset.Live, AssertedUpdate(id, asserted, dropFirst: true), cancellationToken).ConfigureAwait(false);
            inferred = await Inference.ConsequencesAsync(_store, Inference.SubjectsOf(asserted), staged.InferencePredicates, cancellationToken).ConfigureAwait(false);
        }

        var quarantined = accepted ? 0 : staged.Graph.Graph.Triples.Count;
        if (accepted)
        {
            await _store.DeleteGraphAsync(Dataset.Quarantine, id.QuarantineGraph, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.PutGraphAsync(Dataset.Quarantine, id.QuarantineGraph, staged.Graph.Graph, cancellationToken).ConfigureAwait(false);
        }

        // Step 4: provenance, with the findings and the sizes known.
        var report = IngestionReport.From(id.Value, staged.Report.VocabularyVersion, findings,
            staged.Report.Statistics with { Asserted = asserted.Triples.Count, Inferred = inferred.Triples.Count, CommitMs = watch.ElapsedMilliseconds });
        var provenance = Provenance.Build(id, staged.ManifestName, staged.ContentHash, staged.ManifestJson, report, _clock.GetUtcNow(), asserted.Triples.Count, inferred.Triples.Count, quarantined);
        var inferredUpdate = new StringBuilder()
            .Append("INSERT DATA { GRAPH <").Append(id.InferredGraph).Append("> {\n").Append(NTriples(inferred)).Append("} } ;\n")
            .Append("INSERT DATA { GRAPH <").Append(id.ProvenanceGraph).Append("> {\n").Append(NTriples(provenance)).Append("} }\n")
            .ToString();
        await _store.UpdateAsync(Dataset.Live, inferredUpdate, cancellationToken).ConfigureAwait(false);

        // The write is the bulk of the commit, so its time is recorded after it, as one triple of the provenance.
        var commitMs = watch.ElapsedMilliseconds;
        await _store.UpdateAsync(Dataset.Live, Provenance.CommitTimeUpdate(id, commitMs), cancellationToken).ConfigureAwait(false);
        return report with { Statistics = report.Statistics with { CommitMs = commitMs } };
    }

    /// <summary>
    /// Prepare and commit in one go (the <c>POST /manifests</c> path, specification 3.4). When the manifest
    /// is identical to the live one of the same id, the stored report comes back and nothing is written.
    /// </summary>
    public async Task<IngestionReport> InjectAsync(Blackboard.Contracts.Manifests.Manifest manifest, VocabularyModel vocabulary, ValidationPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var id = ManifestId.Of(manifest);
        var (_, hash) = Canonical(manifest);
        var live = await ReadStoredAsync(id, cancellationToken).ConfigureAwait(false);
        if (live is not null && live.Value.Hash == hash)
        {
            return live.Value.Report;
        }

        var staged = Prepare(manifest, vocabulary, policy);
        return await CommitAsync(staged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every live manifest, from the provenance graphs, ordered by id.</summary>
    public async Task<IReadOnlyList<ManifestSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _store.QueryAsync(Dataset.Live, Provenance.ListQuery, QueryOptions.Default, cancellationToken).ConfigureAwait(false);
        return result.Table is null ? [] : result.Table.Results.Select(Provenance.Read).OfType<ManifestSummary>().ToList();
    }

    /// <summary>The stored report of a manifest, or null when the id is not live.</summary>
    public async Task<IngestionReport?> GetReportAsync(ManifestId id, CancellationToken cancellationToken = default)
    {
        var stored = await ReadStoredAsync(id, cancellationToken).ConfigureAwait(false);
        return stored?.Report;
    }

    private async Task<(string Hash, IngestionReport Report)?> ReadStoredAsync(ManifestId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        var result = await _store.QueryAsync(Dataset.Live, Provenance.ReportQuery(id), QueryOptions.Default, cancellationToken).ConfigureAwait(false);
        var row = result.Table?.Results.FirstOrDefault();
        if (row is null || row["hash"] is not ILiteralNode hash || row["report"] is not ILiteralNode report)
        {
            return null;
        }

        var stored = ReportJson.Parse(report.Value);
        if (row.HasBoundValue("commitMs") && row["commitMs"] is ILiteralNode commit && long.TryParse(commit.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var commitMs))
        {
            stored = stored with { Statistics = stored.Statistics with { CommitMs = commitMs } };
        }

        return (hash.Value, stored);
    }

    private static string AssertedUpdate(ManifestId id, IGraph asserted, bool dropFirst)
    {
        var update = new StringBuilder();
        if (dropFirst)
        {
            update.Append("DROP SILENT GRAPH <").Append(id.AssertedGraph).Append("> ;\n")
                .Append("DROP SILENT GRAPH <").Append(id.InferredGraph).Append("> ;\n")
                .Append("DROP SILENT GRAPH <").Append(id.ProvenanceGraph).Append("> ;\n");
        }

        return update.Append("INSERT DATA { GRAPH <").Append(id.AssertedGraph).Append("> {\n").Append(NTriples(asserted)).Append("} }\n").ToString();
    }

    private static string NTriples(IGraph graph)
    {
        var writer = new NTriplesWriter(NTriplesSyntax.Rdf11);
        using var text = new System.IO.StringWriter();
        writer.Save(graph, text);
        return text.ToString();
    }
}
