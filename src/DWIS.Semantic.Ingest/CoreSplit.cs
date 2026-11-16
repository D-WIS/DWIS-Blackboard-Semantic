using DWIS.Blackboard.Contracts;
using DWIS.Semantic.Manifest;
using VDS.RDF;

namespace DWIS.Semantic.Ingest;

/// <summary>The two parts of an asserted graph (Appendix C.5).</summary>
/// <param name="Core">The provider, the provided variables with their validity flags, the nodes that carry a provided variable, and the triples among them.</param>
/// <param name="Rest">Everything else.</param>
public sealed record SplitGraph(IGraph Core, IGraph Rest);

/// <summary>
/// Appendix C.5: the core of a manifest is what the real-time process needs to exist whatever the
/// findings: the provider, the provided variables (validity flags included), every node reached from a
/// provided variable by an inverse <c>HasDynamicValue</c>, and the triples among that set
/// (<c>IsProvidedBy</c>, <c>HasDynamicValue</c>, <c>IsValidBy</c>, <c>rdf:type</c>, the provider's own
/// attributes). The rest goes to quarantine when there is an error (R-SEM-5).
/// </summary>
public static class CoreSplit
{
    /// <summary>The verb from a data point to its signal.</summary>
    public const string HasDynamicValue = Namespaces.Canonical + "HasDynamicValue";

    /// <summary>Splits the asserted graph.</summary>
    public static SplitGraph Split(ManifestGraph manifestGraph)
    {
        ArgumentNullException.ThrowIfNull(manifestGraph);
        var graph = manifestGraph.Graph;

        var provider = graph.CreateUriNode(new Uri(manifestGraph.ProviderIri));
        var coreNodes = new HashSet<INode> { provider };
        var isProvidedBy = graph.CreateUriNode(new Uri(Vocabulary.IsProvidedBy));
        var dynamicValue = graph.CreateUriNode(new Uri(HasDynamicValue));
        var signalClass = graph.CreateUriNode(new Uri(Vocabulary.DynamicDrillingSignal));
        var rdfType = graph.CreateUriNode(new Uri(Vocabulary.RdfType));

        // Provided variables: typed DynamicDrillingSignal and provided by this provider (ManifestGraphBuilder).
        foreach (var triple in graph.GetTriplesWithPredicateObject(rdfType, signalClass))
        {
            if (graph.ContainsTriple(new Triple(triple.Subject, isProvidedBy, provider)))
            {
                coreNodes.Add(triple.Subject);
            }
        }

        // Nodes that carry a provided variable.
        foreach (var triple in graph.GetTriplesWithPredicate(dynamicValue).ToList())
        {
            if (coreNodes.Contains(triple.Object))
            {
                coreNodes.Add(triple.Subject);
            }
        }

        var core = new Graph();
        var rest = new Graph();
        foreach (var triple in graph.Triples)
        {
            var inCore = coreNodes.Contains(triple.Subject)
                && (triple.Predicate.Equals(rdfType)
                    || coreNodes.Contains(triple.Object)
                    || (triple.Subject.Equals(provider) && triple.Object.NodeType == NodeType.Literal));
            (inCore ? core : rest).Assert(triple);
        }

        return new SplitGraph(core, rest);
    }
}
