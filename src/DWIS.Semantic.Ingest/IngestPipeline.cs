using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;
using DWIS.Semantic.Store;
using DWIS.Semantic.Validation;
using Microsoft.Extensions.Logging;
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

/// <summary>A provided variable the real-time process creates, with its validity flag (R-RT-4).</summary>
/// <param name="VariableId">The id as the manifest names it.</param>
/// <param name="DataType">The data type as the manifest names it.</param>
/// <param name="Rank">The rank.</param>
/// <param name="Dimensions">The dimensions, or null.</param>
/// <param name="InitialValue">The initial value text.</param>
/// <param name="ValidityVariableId">The id of the validity boolean next to it, or null when the variable is itself a flag.</param>
public sealed record VariableToCreate(string VariableId, string DataType, int Rank, int[]? Dimensions, string InitialValue, string? ValidityVariableId);

/// <summary>What <c>PrepareManifest</c> answers (specification 3.3): the report of the pre-processing pass and the variables to create.</summary>
/// <param name="Id">The manifest.</param>
/// <param name="ContentHash">The hash of the manifest in its canonical JSON form.</param>
/// <param name="Report">The report so far; the commit adds the re-check and the statistics.</param>
/// <param name="AlreadyLive">True when identical content is live: nothing was staged and no commit is needed.</param>
/// <param name="Variables">The provided variables with their validity flags, always, accepted or not.</param>
public sealed record PrepareOutcome(ManifestId Id, string ContentHash, IngestionReport Report, bool AlreadyLive, IReadOnlyList<VariableToCreate> Variables);

/// <summary>What re-deriving did to one manifest (Appendix C.7).</summary>
/// <param name="Id">The manifest.</param>
/// <param name="AcceptedBefore">Whether the whole manifest was live before.</param>
/// <param name="Report">The new report; its <see cref="IngestionReport.Accepted"/> says whether the whole manifest is live now.</param>
public sealed record RederivedManifest(ManifestId Id, bool AcceptedBefore, IngestionReport Report)
{
    /// <summary>True when the manifest was live whole and is core-only now: the reason to roll a vocabulary version back.</summary>
    public bool BecameCoreOnly => AcceptedBefore && !Report.Accepted;
}

/// <summary>
/// The injection pipeline of specification 4 and Appendix C: prepare (C.2 to C.5, pure) and commit
/// (C.6 steps 1 to 4: the asserted graph, then the inferred graph read from the inference view, then the
/// re-check on both with a downgrade to core-only when it finds a new error, then the provenance; the
/// quarantine graph when rejected). After every change to the live description (commit, remove, re-derive) the
/// observers are told, which is where the registered queries are re-evaluated (M3.1). The two phases of the
/// gRPC contract are <see cref="StageAsync"/> and <see cref="CommitStagedAsync"/> over the staging table (M3.3).
/// Injecting the same manifest twice returns the stored report without re-processing (C.5).
/// </summary>
public sealed class IngestPipeline
{
    private readonly ITripleStore _store;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<ILiveDescriptionObserver> _observers;
    private readonly ILogger<IngestPipeline>? _logger;
    private readonly StagingTable _staging;

    /// <summary>Creates the pipeline over a store, with the observers told after every change (none by default) and the staging table (60 s by default).</summary>
    public IngestPipeline(ITripleStore store, TimeProvider? clock = null, IEnumerable<ILiveDescriptionObserver>? observers = null, ILogger<IngestPipeline>? logger = null, StagingTable? staging = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _observers = observers?.ToList() ?? [];
        _logger = logger;
        _staging = staging ?? new StagingTable(_clock, TimeSpan.FromSeconds(60));
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
    /// Writes the staged manifest to the live dataset (C.6, C.7). First update: what a previous injection
    /// of the same id left is dropped and the new asserted triples (whole when accepted, core only when
    /// not) go to the staging graph, where the inference view reasons over them. Then the inferred graph is
    /// read from the view, and the re-check of the disjointness and the shapes runs on both graphs: a new
    /// error downgrades the manifest to core-only (the staging graph is rewritten as the core and the
    /// consequences recomputed). Second update: the staging graph becomes the asserted graph and the
    /// inferred and provenance graphs land with it, in one request, so that a reader sees either no
    /// manifest or the whole new one, never a mix (T-SEM-11). The whole manifest graph goes to the
    /// quarantine dataset when the manifest is not accepted, else no quarantine graph. The observers are told
    /// once the manifest is live.
    /// </summary>
    public async Task<IngestionReport> CommitAsync(StagedManifest staged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var report = await CommitCoreAsync(staged, Provenance.LocalOrigin, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(new DescriptionChange(DescriptionChangeKind.Committed, [staged.Id]), cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <param name="staged">The prepared manifest.</param>
    /// <param name="origin">What the provenance records as the origin: <c>local</c>, or what the real-time process said at prepare (a peer's manifest from the hub, R-HUB-3).</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    private async Task<IngestionReport> CommitCoreAsync(StagedManifest staged, string origin, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var id = staged.Id;
        var accepted = staged.Accepted;
        var asserted = accepted ? staged.Graph.Graph : staged.Split.Core;

        // Step 1: drop the previous injection of this id and stage the new asserted triples.
        await _store.UpdateAsync(Dataset.Live, DropAll(id) + StagingInsert(id, asserted), cancellationToken).ConfigureAwait(false);

        // Step 2: what the reasoner adds about this provider's resources, now that the staged triples are
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
            await _store.UpdateAsync(Dataset.Live, $"DROP SILENT GRAPH <{id.StagingGraph}> ;\n" + StagingInsert(id, asserted), cancellationToken).ConfigureAwait(false);
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
        var provenance = Provenance.Build(id, staged.ManifestName, staged.ContentHash, staged.ManifestJson, report, _clock.GetUtcNow(), asserted.Triples.Count, inferred.Triples.Count, quarantined, origin);
        var publish = new StringBuilder()
            .Append("DROP SILENT GRAPH <").Append(id.StagingGraph).Append("> ;\n")
            .Append("INSERT DATA { GRAPH <").Append(id.AssertedGraph).Append("> {\n").Append(NTriples(asserted)).Append("} } ;\n")
            .Append("INSERT DATA { GRAPH <").Append(id.InferredGraph).Append("> {\n").Append(NTriples(inferred)).Append("} } ;\n")
            .Append("INSERT DATA { GRAPH <").Append(id.ProvenanceGraph).Append("> {\n").Append(NTriples(provenance)).Append("} }\n")
            .ToString();
        await _store.UpdateAsync(Dataset.Live, publish, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Removes a manifest (C.7): its asserted, inferred, provenance and staging graphs in one update, and
    /// its quarantine graph. False when the id is not live (nothing is touched then). The endpoints graph
    /// goes with it. The observers are told once the graphs are gone.
    /// </summary>
    public async Task<bool> RemoveAsync(ManifestId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var live = await _store.QueryAsync(Dataset.Live, $"ASK {{ GRAPH <{id.ProvenanceGraph}> {{ ?s ?p ?o }} }}", QueryOptions.Default, cancellationToken).ConfigureAwait(false);
        if (live.Table?.Result != true)
        {
            return false;
        }

        var stored = await GetStoredManifestAsync(id, cancellationToken).ConfigureAwait(false);
        var endpoints = stored is null ? "" : Endpoints.DeleteUpdate(Endpoints.VariableIris(stored));
        await _store.UpdateAsync(Dataset.Live, DropAll(id) + endpoints, cancellationToken).ConfigureAwait(false);
        await _store.DeleteGraphAsync(Dataset.Quarantine, id.QuarantineGraph, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(new DescriptionChange(DescriptionChangeKind.Removed, [id]), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The manifest as it was received, from its provenance; null when the id is not live.</summary>
    public async Task<Blackboard.Contracts.Manifests.Manifest?> GetStoredManifestAsync(ManifestId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var result = await _store.QueryAsync(Dataset.Live, Provenance.ManifestQuery(id), QueryOptions.Default, cancellationToken).ConfigureAwait(false);
        var row = result.Table?.Results.FirstOrDefault();
        return row is not null && row["manifest"] is ILiteralNode json ? ManifestJson.Parse(json.Value) : null;
    }

    /// <summary>
    /// Re-derives every live manifest against a vocabulary (Appendix C.7, the vocabulary replaced): each is
    /// prepared and committed again from the manifest JSON kept with its provenance, so the pre-processing
    /// findings, the inferred graph and the re-check all follow the new vocabulary and shapes. Returns one
    /// entry per manifest, in id order, saying whether it became core-only. Manifests are done one at a time;
    /// the observers are told once, at the end, when at least one manifest was re-derived.
    /// </summary>
    public async Task<IReadOnlyList<RederivedManifest>> RederiveAsync(VocabularyModel vocabulary, ValidationPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        var result = new List<RederivedManifest>();
        foreach (var live in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var manifest = await GetStoredManifestAsync(live.Id, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            // The origin stays what it was: a re-derivation changes the vocabulary, not where the manifest came from.
            var report = await CommitCoreAsync(Prepare(manifest, vocabulary, policy), live.Origin, cancellationToken).ConfigureAwait(false);
            result.Add(new RederivedManifest(live.Id, live.Accepted, report));
        }

        if (result.Count > 0)
        {
            await NotifyAsync(new DescriptionChange(DescriptionChangeKind.Rederived, result.Select(r => r.Id).ToList()), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// The first phase of the two-phase injection (specification 3.3, <c>PrepareManifest</c>): prepare and
    /// stage, nothing written. Identical content already live comes back as such, with the stored report,
    /// and nothing is staged. The entry waits in the staging table for <see cref="CommitStagedAsync"/> or
    /// <see cref="Abort"/>, for the table's time limit.
    /// </summary>
    public async Task<PrepareOutcome> StageAsync(Blackboard.Contracts.Manifests.Manifest manifest, VocabularyModel vocabulary, ValidationPolicy? policy, string origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var id = ManifestId.Of(manifest);
        var (_, hash) = Canonical(manifest);
        var variables = VariablesToCreate(manifest);
        var live = await ReadStoredAsync(id, cancellationToken).ConfigureAwait(false);
        if (live is not null && live.Value.Hash == hash)
        {
            return new PrepareOutcome(id, hash, live.Value.Report, true, variables);
        }

        var staged = Prepare(manifest, vocabulary, policy);
        _staging.Stage(staged, origin);
        return new PrepareOutcome(id, hash, staged.Report, false, variables);
    }

    /// <summary>
    /// The second phase (<c>CommitManifest</c>): the staged manifest goes live (C.6), its endpoints are
    /// written, and the observers are told. Null when the id is not staged or its entry has expired.
    /// </summary>
    public async Task<IngestionReport?> CommitStagedAsync(ManifestId id, IReadOnlyList<VariableEndpoint> endpoints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(endpoints);
        var entry = _staging.Take(id);
        if (entry is null)
        {
            return null;
        }

        var report = await CommitCoreAsync(entry.Manifest, entry.Origin, cancellationToken).ConfigureAwait(false);
        var provider = id.Provider;
        var update = Endpoints.DeleteUpdate(endpoints.Select(e => ManifestIris.ProvidedVariable(provider, e.VariableId)))
            + "INSERT DATA { GRAPH <" + Endpoints.Graph + "> {\n" + NTriples(Endpoints.Build(id, provider, endpoints)) + "} }\n";
        if (endpoints.Count > 0)
        {
            await _store.UpdateAsync(Dataset.Live, update, cancellationToken).ConfigureAwait(false);
        }

        await NotifyAsync(new DescriptionChange(DescriptionChangeKind.Committed, [id]), cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <summary>Drops a staged manifest (<c>AbortManifest</c>); false when the id is not staged.</summary>
    public bool Abort(ManifestId id) => _staging.Abort(id);

    /// <summary>The staging table, for its count and its time limit.</summary>
    public StagingTable Staging => _staging;

    /// <summary>The endpoints the real-time process reported for a manifest at its commit; empty when none or not live.</summary>
    public async Task<IReadOnlyList<VariableEndpoint>> GetEndpointsAsync(ManifestId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var result = await _store.QueryAsync(Dataset.Live, Endpoints.Query(id), QueryOptions.Default, cancellationToken).ConfigureAwait(false);
        return result.Table is null ? [] : result.Table.Results.Select(r => Endpoints.Read(r, id.Provider)).ToList();
    }

    /// <summary>The provided variables of a manifest as the real-time process creates them, flags completed (C.2).</summary>
    public static IReadOnlyList<VariableToCreate> VariablesToCreate(Blackboard.Contracts.Manifests.Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var completed = ManifestPreparation.Prepare(manifest).Completed.ProvidedVariables;
        var ids = completed.Select(v => v.VariableID).ToHashSet(StringComparer.Ordinal);
        return completed
            .Select(v => new VariableToCreate(v.VariableID, v.DataType, v.Rank, v.Dimensions, v.InitialValue,
                ids.Contains(ManifestIris.ValidityFlagId(v.VariableID)) ? ManifestIris.ValidityFlagId(v.VariableID) : null))
            .ToList();
    }

    private async Task NotifyAsync(DescriptionChange change, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.ChangedAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The change is written; an observer that fails must not make the caller believe it is not.
                if (_logger is not null)
                {
                    Log.ObserverFailed(_logger, ex, change.Kind, change.Manifests.Count);
                }
            }
        }
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

    private static string DropAll(ManifestId id) =>
        $"DROP SILENT GRAPH <{id.AssertedGraph}> ;\nDROP SILENT GRAPH <{id.InferredGraph}> ;\nDROP SILENT GRAPH <{id.ProvenanceGraph}> ;\nDROP SILENT GRAPH <{id.StagingGraph}> ;\n";

    private static string StagingInsert(ManifestId id, IGraph asserted) =>
        new StringBuilder().Append("INSERT DATA { GRAPH <").Append(id.StagingGraph).Append("> {\n").Append(NTriples(asserted)).Append("} }\n").ToString();

    private static string NTriples(IGraph graph)
    {
        var writer = new NTriplesWriter(NTriplesSyntax.Rdf11);
        using var text = new System.IO.StringWriter();
        writer.Save(graph, text);
        return text.ToString();
    }
}
