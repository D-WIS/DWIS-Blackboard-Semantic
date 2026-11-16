using System.Globalization;
using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Reports;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace DWIS.Semantic.Ingest;

/// <summary>What the provenance graph of a manifest says (Appendix C.8), as read back from the store.</summary>
/// <param name="Id">The manifest id.</param>
/// <param name="ManifestName">The manifest name as written.</param>
/// <param name="ContentHash">SHA-256 of the manifest JSON, <c>sha256:</c> and 64 hex digits.</param>
/// <param name="VocabularyVersion">The vocabulary the manifest was checked against.</param>
/// <param name="InjectedAt">When it was committed.</param>
/// <param name="Accepted">True when the whole manifest went live.</param>
/// <param name="Counts">The finding counts of the report.</param>
/// <param name="AssertedTriples">Triples in the asserted graph.</param>
/// <param name="InferredTriples">Triples in the inferred graph.</param>
/// <param name="QuarantinedTriples">Triples in the quarantine graph (zero when accepted).</param>
public sealed record ManifestSummary(
    ManifestId Id,
    string ManifestName,
    string ContentHash,
    string VocabularyVersion,
    DateTimeOffset InjectedAt,
    bool Accepted,
    FindingCounts Counts,
    int AssertedTriples,
    int InferredTriples,
    int QuarantinedTriples);

/// <summary>
/// The service's own terms for the provenance graph, under <c>http://d-wis.org/blackboard/</c> so that
/// they never collide with the vocabulary's. Subject: the asserted graph IRI of the manifest.
/// </summary>
public static class Provenance
{
    /// <summary>The namespace of the service's own terms.</summary>
    public const string Namespace = Namespaces.Canonical + "blackboard/";

    /// <summary>The class of the provenance subject.</summary>
    public const string Injection = Namespace + "Injection";

    /// <summary>Property: the manifest id.</summary>
    public const string ManifestIdProperty = Namespace + "manifestId";

    /// <summary>Property: the provider name.</summary>
    public const string ProviderProperty = Namespace + "provider";

    /// <summary>Property: the manifest name as written.</summary>
    public const string ManifestNameProperty = Namespace + "manifestName";

    /// <summary>Property: the content hash.</summary>
    public const string ContentHashProperty = Namespace + "contentHash";

    /// <summary>Property: the vocabulary version.</summary>
    public const string VocabularyVersionProperty = Namespace + "vocabularyVersion";

    /// <summary>Property: when the manifest was committed.</summary>
    public const string InjectedAtProperty = Namespace + "injectedAt";

    /// <summary>Property: where the injection came from (<c>local</c> in this project; a hub site later).</summary>
    public const string OriginProperty = Namespace + "origin";

    /// <summary>Property: whether the whole manifest went live.</summary>
    public const string AcceptedProperty = Namespace + "accepted";

    /// <summary>Property: error count.</summary>
    public const string ErrorsProperty = Namespace + "errors";

    /// <summary>Property: warning count.</summary>
    public const string WarningsProperty = Namespace + "warnings";

    /// <summary>Property: info count.</summary>
    public const string InfosProperty = Namespace + "infos";

    /// <summary>Property: triples in the asserted graph.</summary>
    public const string AssertedTriplesProperty = Namespace + "assertedTriples";

    /// <summary>Property: triples in the inferred graph.</summary>
    public const string InferredTriplesProperty = Namespace + "inferredTriples";

    /// <summary>Property: triples in the quarantine graph.</summary>
    public const string QuarantinedTriplesProperty = Namespace + "quarantinedTriples";

    /// <summary>Property: the quarantine graph IRI, present when the manifest was rejected.</summary>
    public const string QuarantineGraphProperty = Namespace + "quarantineGraph";

    /// <summary>Property: the report of Appendix C.9 as a JSON literal (its commit time is in <see cref="CommitMsProperty"/>, written after the commit).</summary>
    public const string ReportProperty = Namespace + "report";

    /// <summary>Property: milliseconds the commit took, written once the write is done.</summary>
    public const string CommitMsProperty = Namespace + "commitMs";

    /// <summary>Property: the manifest as a JSON literal, as received.</summary>
    public const string ManifestProperty = Namespace + "manifest";

    /// <summary>Builds the provenance graph of one injection.</summary>
    public static Graph Build(ManifestId id, string manifestName, string contentHash, string manifestJson, IngestionReport report, DateTimeOffset injectedAt, int assertedTriples, int inferredTriples, int quarantinedTriples)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(report);
        var graph = new Graph();
        var subject = graph.CreateUriNode(new Uri(id.AssertedGraph));
        graph.Assert(subject, graph.CreateUriNode(new Uri(RdfSpecsHelper.RdfType)), graph.CreateUriNode(new Uri(Injection)));
        graph.Assert(subject, Iri(graph, ManifestIdProperty), graph.CreateLiteralNode(id.Value));
        graph.Assert(subject, Iri(graph, ProviderProperty), graph.CreateLiteralNode(id.Provider));
        graph.Assert(subject, Iri(graph, ManifestNameProperty), graph.CreateLiteralNode(manifestName));
        graph.Assert(subject, Iri(graph, ContentHashProperty), graph.CreateLiteralNode(contentHash));
        graph.Assert(subject, Iri(graph, VocabularyVersionProperty), graph.CreateLiteralNode(report.VocabularyVersion ?? ""));
        graph.Assert(subject, Iri(graph, InjectedAtProperty), graph.CreateLiteralNode(injectedAt.ToString("o", CultureInfo.InvariantCulture), new Uri(XmlSpecsHelper.XmlSchemaDataTypeDateTime)));
        graph.Assert(subject, Iri(graph, OriginProperty), graph.CreateLiteralNode("local"));
        graph.Assert(subject, Iri(graph, AcceptedProperty), Boolean(graph, report.Accepted));
        graph.Assert(subject, Iri(graph, ErrorsProperty), Integer(graph, report.Counts.Errors));
        graph.Assert(subject, Iri(graph, WarningsProperty), Integer(graph, report.Counts.Warnings));
        graph.Assert(subject, Iri(graph, InfosProperty), Integer(graph, report.Counts.Infos));
        graph.Assert(subject, Iri(graph, AssertedTriplesProperty), Integer(graph, assertedTriples));
        graph.Assert(subject, Iri(graph, InferredTriplesProperty), Integer(graph, inferredTriples));
        graph.Assert(subject, Iri(graph, QuarantinedTriplesProperty), Integer(graph, quarantinedTriples));
        if (!report.Accepted)
        {
            graph.Assert(subject, Iri(graph, QuarantineGraphProperty), graph.CreateUriNode(new Uri(id.QuarantineGraph)));
        }

        graph.Assert(subject, Iri(graph, ReportProperty), graph.CreateLiteralNode(ReportJson.Serialize(report)));
        graph.Assert(subject, Iri(graph, ManifestProperty), graph.CreateLiteralNode(manifestJson));
        return graph;
    }

    /// <summary>The SPARQL that lists every live manifest, one row each, ordered by id.</summary>
    public static string ListQuery { get; } = $$"""
        SELECT ?id ?name ?hash ?version ?at ?accepted ?errors ?warnings ?infos ?asserted ?inferred ?quarantined
        WHERE {
          GRAPH ?g {
            ?m a <{{Injection}}> ;
               <{{ManifestIdProperty}}> ?id ;
               <{{ManifestNameProperty}}> ?name ;
               <{{ContentHashProperty}}> ?hash ;
               <{{VocabularyVersionProperty}}> ?version ;
               <{{InjectedAtProperty}}> ?at ;
               <{{AcceptedProperty}}> ?accepted ;
               <{{ErrorsProperty}}> ?errors ;
               <{{WarningsProperty}}> ?warnings ;
               <{{InfosProperty}}> ?infos ;
               <{{AssertedTriplesProperty}}> ?asserted ;
               <{{InferredTriplesProperty}}> ?inferred ;
               <{{QuarantinedTriplesProperty}}> ?quarantined .
          }
        }
        ORDER BY ?id
        """;

    /// <summary>The SPARQL that reads the content hash, the stored report and the commit time of one manifest.</summary>
    public static string ReportQuery(ManifestId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"SELECT ?hash ?report ?commitMs WHERE {{ GRAPH <{id.ProvenanceGraph}> {{ <{id.AssertedGraph}> <{ContentHashProperty}> ?hash ; <{ReportProperty}> ?report . OPTIONAL {{ <{id.AssertedGraph}> <{CommitMsProperty}> ?commitMs }} }} }}";
    }

    /// <summary>The SPARQL update that records the commit time once the write is done.</summary>
    public static string CommitTimeUpdate(ManifestId id, long commitMs)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"WITH <{id.ProvenanceGraph}> DELETE {{ <{id.AssertedGraph}> <{CommitMsProperty}> ?old }} INSERT {{ <{id.AssertedGraph}> <{CommitMsProperty}> {commitMs.ToString(CultureInfo.InvariantCulture)} }} WHERE {{ OPTIONAL {{ <{id.AssertedGraph}> <{CommitMsProperty}> ?old }} }}";
    }

    /// <summary>Reads one row of <see cref="ListQuery"/>; null when the row is not well formed (a foreign graph, for example).</summary>
    public static ManifestSummary? Read(VDS.RDF.Query.ISparqlResult row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var id = ManifestId.Parse(Text(row, "id"));
        if (id is null)
        {
            return null;
        }

        return new ManifestSummary(
            id,
            Text(row, "name"),
            Text(row, "hash"),
            Text(row, "version"),
            DateTimeOffset.TryParse(Text(row, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue,
            string.Equals(Text(row, "accepted"), "true", StringComparison.OrdinalIgnoreCase),
            new FindingCounts(Int(row, "errors"), Int(row, "warnings"), Int(row, "infos")),
            Int(row, "asserted"),
            Int(row, "inferred"),
            Int(row, "quarantined"));
    }

    private static string Text(VDS.RDF.Query.ISparqlResult row, string variable) =>
        row.HasBoundValue(variable) && row[variable] is ILiteralNode literal ? literal.Value : row.HasBoundValue(variable) ? row[variable].ToString() ?? "" : "";

    private static int Int(VDS.RDF.Query.ISparqlResult row, string variable) =>
        int.TryParse(Text(row, variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static IUriNode Iri(Graph graph, string iri) => graph.CreateUriNode(new Uri(iri));

    private static ILiteralNode Boolean(Graph graph, bool value) =>
        graph.CreateLiteralNode(value ? "true" : "false", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));

    private static ILiteralNode Integer(Graph graph, int value) =>
        graph.CreateLiteralNode(value.ToString(CultureInfo.InvariantCulture), new Uri(XmlSpecsHelper.XmlSchemaDataTypeInteger));
}
