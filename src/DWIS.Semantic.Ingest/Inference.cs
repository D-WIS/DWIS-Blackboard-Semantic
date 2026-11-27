using System.Reflection;
using DWIS.Semantic.Store;
using VDS.RDF;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;

namespace DWIS.Semantic.Ingest;

/// <summary>
/// Appendix C.6 step 2 (decision D-64): the consequences of one manifest, derived in the store by the
/// SPARQL updates of <c>inference-rules.sparql</c> over the closed schema (decision D-47: the vocabulary
/// and the units graphs are loaded closed, so every rule is one step), from the staging graph into a
/// scratch graph, then read back by the answer query of <c>inference-answer.sparql</c>, which keeps the
/// manifest's subjects, leaves out what any non-inferred graph already asserts and the reasoner's trivia.
/// Both texts are embedded here and can be replaced by files (the <c>Inference</c> section of the
/// configuration), so that the rules stay data.
/// </summary>
/// <remarks>
/// Until milestone 5 the consequences came from the inference view <c>/dwis-inf</c> (the OWL micro
/// reasoner over the whole store), asked per subject and predicate: 18 to 20 s for the SmartConnect
/// manifest, because that reasoner tries one rule per domain and range axiom for every subject. The
/// updates give the same triples in under 3 s (M5.6, 27 November 2026; the DWISVerb values and the
/// DWISNoun types left out since, decision D-64).
/// </remarks>
public sealed class Inference
{
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string RdfsResource = "http://www.w3.org/2000/01/rdf-schema#Resource";
    private const string OwlThing = "http://www.w3.org/2002/07/owl#Thing";
    private const string SourcePlaceholder = "{source}";
    private const string ScratchPlaceholder = "{scratch}";

    /// <summary>The limits of the answer query: generous, since a manifest's consequences are a few thousand triples.</summary>
    public static QueryOptions Options { get; } = new() { Timeout = TimeSpan.FromMinutes(2), RowLimit = 1_000_000 };

    /// <summary>Creates the inference over the given rule and answer texts (see <see cref="Default"/> and <see cref="FromFiles"/>).</summary>
    public Inference(string rules, string answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        foreach (var (name, text) in new[] { ("rules", rules), ("answer", answer) })
        {
            if (!text.Contains(SourcePlaceholder, StringComparison.Ordinal) || !text.Contains(ScratchPlaceholder, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The inference {name} must name the staging graph as {SourcePlaceholder} and the scratch graph as {ScratchPlaceholder}.");
            }
        }

        Rules = rules;
        Answer = answer;
    }

    /// <summary>The embedded rules and answer.</summary>
    public static Inference Default { get; } = new(Embedded("inference-rules.sparql"), Embedded("inference-answer.sparql"));

    /// <summary>The SPARQL update text of the rules, with the placeholders.</summary>
    public string Rules { get; }

    /// <summary>The SPARQL CONSTRUCT text of the answer, with the placeholders.</summary>
    public string Answer { get; }

    /// <summary>The rules and the answer from files; an empty path keeps the embedded text.</summary>
    public static Inference FromFiles(string? rulesFile, string? answerFile) =>
        new(string.IsNullOrWhiteSpace(rulesFile) ? Default.Rules : File.ReadAllText(rulesFile),
            string.IsNullOrWhiteSpace(answerFile) ? Default.Answer : File.ReadAllText(answerFile));

    /// <summary>The rules for one manifest: the placeholders replaced by its staging and scratch graphs.</summary>
    public string RulesFor(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Fill(Rules, id);
    }

    /// <summary>The answer query for one manifest.</summary>
    public string AnswerFor(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Fill(Answer, id);
    }

    /// <summary>
    /// Runs the rules from the manifest's staging graph into its scratch graph and reads the answer back, as
    /// the inferred graph. The scratch graph is left for the commit's last update to drop with the staging graph.
    /// </summary>
    public async Task<Graph> ConsequencesAsync(ITripleStore store, ManifestId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(id);
        await store.UpdateAsync(Dataset.Live, RulesFor(id), cancellationToken).ConfigureAwait(false);
        var answer = await store.QueryAsync(Dataset.Live, AnswerFor(id), Options, cancellationToken).ConfigureAwait(false);
        if (answer.Graph is null)
        {
            throw new StoreException("The inference answer did not return a graph.");
        }

        if (answer.Truncated)
        {
            throw new StoreException($"More than {Options.RowLimit} inferred triples for {id.Value}; the inferred graph would be incomplete.");
        }

        return Trim(answer.Graph);
    }

    /// <summary>
    /// The triples without the reasoner's trivia, should any reach here: a type of <c>owl:Thing</c> or
    /// <c>rdfs:Resource</c>, or a blank-node object (an anonymous class expression of the vocabulary,
    /// meaningless outside the derivation). The answer query filters them already; this keeps the rule even
    /// when the answer file is replaced.
    /// </summary>
    public static Graph Trim(IGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var kept = new Graph();
        foreach (var triple in graph.Triples)
        {
            if (!IsTrivia(triple))
            {
                kept.Assert(triple);
            }
        }

        return kept;
    }

    private static string Fill(string text, ManifestId id) =>
        text.Replace(SourcePlaceholder, id.StagingGraph, StringComparison.Ordinal).Replace(ScratchPlaceholder, id.InferenceScratchGraph, StringComparison.Ordinal);

    private static string Embedded(string name)
    {
        var assembly = typeof(Inference).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsTrivia(Triple triple)
    {
        if (triple.Object.NodeType == NodeType.Blank)
        {
            return true;
        }

        return triple.Predicate is IUriNode predicate && predicate.Uri.AbsoluteUri == RdfType
            && triple.Object is IUriNode type && type.Uri.AbsoluteUri is RdfsResource or OwlThing;
    }
}
