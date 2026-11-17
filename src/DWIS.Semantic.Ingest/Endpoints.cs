using DWIS.Blackboard.Contracts;
using DWIS.Semantic.Manifest;
using VDS.RDF;

namespace DWIS.Semantic.Ingest;

/// <summary>Where a provided variable lives in the real-time process (specification 3.3, R-RT-5).</summary>
/// <param name="VariableId">The variable id as the manifest names it (the validity flag has its own id).</param>
/// <param name="EndpointUrl">The OPC UA endpoint URL (later another transport's address).</param>
/// <param name="NamespaceUri">The namespace URI of the node.</param>
/// <param name="NodeId">The node id in that namespace.</param>
public sealed record VariableEndpoint(string VariableId, string EndpointUrl, string NamespaceUri, string NodeId);

/// <summary>
/// The endpoints graph <c>http://d-wis.org/endpoints</c> (Appendix C.8): one entry per provided variable that
/// the real-time process reported at commit, keyed by the variable's IRI, with the manifest it belongs to.
/// Written at commit, dropped with the manifest at remove; a later transport adds its own properties.
/// </summary>
public static class Endpoints
{
    /// <summary>The graph.</summary>
    public const string Graph = Namespaces.Canonical + "endpoints";

    /// <summary>Property: the endpoint URL.</summary>
    public const string EndpointUrlProperty = Provenance.Namespace + "endpointUrl";

    /// <summary>Property: the namespace URI of the node.</summary>
    public const string NamespaceUriProperty = Provenance.Namespace + "namespaceUri";

    /// <summary>Property: the node id.</summary>
    public const string NodeIdProperty = Provenance.Namespace + "nodeId";

    /// <summary>The IRIs a manifest's variables and their validity flags have in the store.</summary>
    public static IReadOnlyList<string> VariableIris(Blackboard.Contracts.Manifests.Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var provider = manifest.Provider.Name;
        return manifest.ProvidedVariables
            .SelectMany(v => new[] { ManifestIris.ProvidedVariable(provider, v.VariableID), ManifestIris.ProvidedVariable(provider, ManifestIris.ValidityFlagId(v.VariableID)) })
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The triples of the endpoints of one manifest.</summary>
    public static Graph Build(ManifestId id, string provider, IEnumerable<VariableEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoints);
        var graph = new Graph();
        var manifest = graph.CreateUriNode(new Uri(Provenance.ManifestIdProperty));
        var url = graph.CreateUriNode(new Uri(EndpointUrlProperty));
        var ns = graph.CreateUriNode(new Uri(NamespaceUriProperty));
        var node = graph.CreateUriNode(new Uri(NodeIdProperty));
        foreach (var endpoint in endpoints)
        {
            var variable = graph.CreateUriNode(new Uri(ManifestIris.ProvidedVariable(provider, endpoint.VariableId)));
            graph.Assert(variable, manifest, graph.CreateLiteralNode(id.Value));
            graph.Assert(variable, url, graph.CreateLiteralNode(endpoint.EndpointUrl));
            graph.Assert(variable, ns, graph.CreateLiteralNode(endpoint.NamespaceUri));
            graph.Assert(variable, node, graph.CreateLiteralNode(endpoint.NodeId));
        }

        return graph;
    }

    /// <summary>The SPARQL update that drops the endpoints of some variables (empty when there are none).</summary>
    public static string DeleteUpdate(IEnumerable<string> variableIris)
    {
        ArgumentNullException.ThrowIfNull(variableIris);
        var values = string.Join(" ", variableIris.Select(iri => "<" + iri + ">"));
        return values.Length == 0 ? "" : $"WITH <{Graph}> DELETE {{ ?v ?p ?o }} WHERE {{ VALUES ?v {{ {values} }} ?v ?p ?o }} ;\n";
    }

    /// <summary>The SPARQL that reads the endpoints of one manifest.</summary>
    public static string Query(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"SELECT ?v ?url ?ns ?node WHERE {{ GRAPH <{Graph}> {{ ?v <{Provenance.ManifestIdProperty}> \"{id.Value}\" ; <{EndpointUrlProperty}> ?url ; <{NamespaceUriProperty}> ?ns ; <{NodeIdProperty}> ?node }} }} ORDER BY ?v";
    }

    /// <summary>Reads one row of <see cref="Query"/>; the variable id is the IRI's last part after <c>{provider}.</c>.</summary>
    public static VariableEndpoint Read(VDS.RDF.Query.ISparqlResult row, string provider)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(provider);
        var iri = row["v"].ToString() ?? "";
        var prefix = ManifestIris.ProvidedVariable(provider, "");
        var variableId = iri.StartsWith(prefix, StringComparison.Ordinal) ? iri[prefix.Length..] : iri;
        return new VariableEndpoint(variableId, Text(row, "url"), Text(row, "ns"), Text(row, "node"));
    }

    private static string Text(VDS.RDF.Query.ISparqlResult row, string variable) => row[variable] is ILiteralNode literal ? literal.Value : row[variable].ToString() ?? "";
}
