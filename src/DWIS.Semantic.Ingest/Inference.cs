using DWIS.Semantic.Store;
using VDS.RDF;
using ITripleStore = DWIS.Semantic.Store.ITripleStore;

namespace DWIS.Semantic.Ingest;

/// <summary>
/// Appendix C.6 step 2: the consequences of one manifest, read from the inference view (<c>/dwis-inf</c>,
/// a reasoner over the union of the live data) and reduced to what is not asserted anywhere in the live
/// data. The view has no named graphs, so the subtraction is done here: everything the view says about
/// the manifest's subjects, minus everything the live dataset already holds about them.
/// </summary>
/// <remarks>
/// Both the subjects and the predicates are named in the query (<c>VALUES</c>, subjects in batches):
/// a bound subject and predicate is answered by the backward rules in milliseconds, while an unbound
/// predicate makes the OWL micro rules derive <c>owl:differentFrom</c> for every pair of individuals of
/// disjoint classes (259,000 triples for the SmartConnect manifest, found on 16 November 2026), and a
/// filter on the subject's text makes the reasoner derive the whole closure of the store before filtering.
/// The predicates are <c>rdf:type</c> and the vocabulary's object and datatype properties, which is where
/// subclass, domain, range, subproperty and inverse entailments land.
/// </remarks>
public static class Inference
{
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string RdfsResource = "http://www.w3.org/2000/01/rdf-schema#Resource";
    private const string OwlThing = "http://www.w3.org/2002/07/owl#Thing";

    /// <summary>Subjects per query.</summary>
    public const int BatchSize = 200;

    /// <summary>The limits of each query: generous, since a manifest's consequences are a few thousand triples.</summary>
    public static QueryOptions Options { get; } = new() { Timeout = TimeSpan.FromMinutes(2), RowLimit = 1_000_000 };

    /// <summary>Every triple about the given subjects with one of the given predicates.</summary>
    public static string SubjectsQuery(IEnumerable<string> subjects, IEnumerable<string> predicates)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(predicates);
        var s = string.Join(" ", subjects.Select(x => "<" + x + ">"));
        var p = string.Join(" ", predicates.Select(x => "<" + x + ">"));
        return $"CONSTRUCT {{ ?s ?p ?o }} WHERE {{ VALUES ?s {{ {s} }} VALUES ?p {{ {p} }} ?s ?p ?o }}";
    }

    /// <summary>The IRI subjects of a graph, each once.</summary>
    public static IReadOnlyList<string> SubjectsOf(IGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Triples.SubjectNodes.OfType<IUriNode>().Select(n => n.Uri.AbsoluteUri).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The predicates worth asking the view for: <c>rdf:type</c> and the vocabulary's properties.</summary>
    public static IReadOnlyList<string> PredicatesOf(Validation.VocabularyModel vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        return [RdfType, .. vocabulary.ObjectProperties.Concat(vocabulary.DatatypeProperties).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The triples of the view that the live data does not hold, without the reasoner's trivia: a type
    /// of <c>owl:Thing</c> or <c>rdfs:Resource</c>, or a blank-node object (an anonymous class expression
    /// of the vocabulary, meaningless outside the view).
    /// </summary>
    public static Graph Subtract(IGraph view, IGraph live)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(live);
        var inferred = new Graph();
        foreach (var triple in view.Triples)
        {
            if (IsTrivia(triple) || live.ContainsTriple(triple))
            {
                continue;
            }

            inferred.Assert(triple);
        }

        return inferred;
    }

    /// <summary>Reads what the view says about the subjects, in batches, and subtracts what the live data holds about them.</summary>
    public static async Task<Graph> ConsequencesAsync(ITripleStore store, IReadOnlyList<string> subjects, IReadOnlyList<string> predicates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(predicates);
        var inferred = new Graph();
        for (var start = 0; start < subjects.Count; start += BatchSize)
        {
            var query = SubjectsQuery(subjects.Skip(start).Take(BatchSize), predicates);
            var view = await store.QueryAsync(Dataset.Inference, query, Options, cancellationToken).ConfigureAwait(false);
            var live = await store.QueryAsync(Dataset.Live, query, Options, cancellationToken).ConfigureAwait(false);
            if (view.Graph is null || live.Graph is null)
            {
                throw new StoreException("The subjects query did not return a graph.");
            }

            if (view.Truncated || live.Truncated)
            {
                throw new StoreException($"More than {Options.RowLimit} triples about {BatchSize} subjects; the inferred graph would be incomplete.");
            }

            inferred.Merge(Subtract(view.Graph, live.Graph));
        }

        return inferred;
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
