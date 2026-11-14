using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Manifests;
using VDS.RDF;

namespace DWIS.Semantic.Manifest;

/// <summary>The asserted graph of a manifest with what went into it.</summary>
/// <param name="Graph">The triples, all IRIs canonical.</param>
/// <param name="ProviderIri">The provider individual.</param>
/// <param name="Nodes">Injected nodes asserted.</param>
/// <param name="ProvidedVariables">Provided variables asserted (validity flags included).</param>
/// <param name="References">References asserted as triples (class assertions and blank-node sentences included).</param>
/// <param name="BlankNodes">Blank nodes created for references whose object is a vocabulary class.</param>
/// <param name="Notes">References that could not be converted, and why.</param>
public sealed record ManifestGraph(
    IGraph Graph,
    string ProviderIri,
    int Nodes,
    int ProvidedVariables,
    int References,
    int BlankNodes,
    IReadOnlyList<ManifestNote> Notes);

/// <summary>
/// Appendix C.3: turns a prepared manifest into its asserted graph with the IRI scheme of
/// <see cref="ManifestIris"/>. Pure and vocabulary-free: field values become plain literals unless a
/// datatype resolver is given (the pre-processing pass of C.4 knows the vocabulary).
/// </summary>
public static class ManifestGraphBuilder
{
    /// <summary>Note code: a reference names a dropped injected variable.</summary>
    public const string ReferenceToInjectedVariable = "reference-to-injected-variable";

    /// <summary>Note code: a reference uses a namespace alias the manifest does not declare.</summary>
    public const string UnknownNamespaceAlias = "unknown-namespace-alias";

    /// <summary>Note code: a reference has an empty subject, verb or object.</summary>
    public const string IncompleteReference = "incomplete-reference";

    /// <summary>Gives the XML Schema datatype of an attribute, or null for a plain literal.</summary>
    public delegate Uri? AttributeDatatypeResolver(string attributeIri);

    /// <summary>Builds the asserted graph.</summary>
    public static ManifestGraph Build(PreparedManifest prepared, AttributeDatatypeResolver? datatypeOf = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var manifest = prepared.Completed;
        var provider = manifest.Provider.Name;
        var graph = new Graph();
        var notes = new List<ManifestNote>();

        var rdfType = Iri(graph, Vocabulary.RdfType);
        var isProvidedBy = Iri(graph, Vocabulary.IsProvidedBy);
        var providerNode = Iri(graph, ManifestIris.Provider(provider));

        // Provider: type and name (specification C.3; the vocabulary has a ProviderName attribute, no company attribute).
        graph.Assert(providerNode, rdfType, Iri(graph, Vocabulary.DataProvider));
        graph.Assert(providerNode, Iri(graph, Vocabulary.ProviderName), graph.CreateLiteralNode(provider));

        // Provided variables, validity flags included.
        var signalClass = Iri(graph, Vocabulary.DynamicDrillingSignal);
        foreach (var variable in manifest.ProvidedVariables)
        {
            var node = Iri(graph, ManifestIris.ProvidedVariable(provider, variable.VariableID));
            graph.Assert(node, rdfType, signalClass);
            graph.Assert(node, isProvidedBy, providerNode);
        }

        // Injected nodes: type, provider, fields.
        foreach (var injected in manifest.InjectedNodes)
        {
            var node = Iri(graph, ManifestIris.Node(provider, injected.UniqueName));
            graph.Assert(node, rdfType, Iri(graph, ManifestIris.Term(injected.TypeDictionaryURI)));
            graph.Assert(node, isProvidedBy, providerNode);
            foreach (var field in injected.Fields ?? [])
            {
                if (string.IsNullOrEmpty(field.FieldName) || field.FieldValue is null)
                {
                    continue;
                }

                var attribute = ManifestIris.Term(field.FieldName);
                var datatype = datatypeOf?.Invoke(attribute);
                var literal = datatype is null ? graph.CreateLiteralNode(field.FieldValue) : graph.CreateLiteralNode(field.FieldValue, datatype);
                graph.Assert(node, Iri(graph, attribute), literal);
            }
        }

        // References.
        var references = 0;
        var blankNodes = new Dictionary<(string Subject, string Verb, string Class), IBlankNode>();
        var belongsToClass = Vocabulary.BelongsToClass;
        foreach (var reference in manifest.InjectedReferences)
        {
            if (string.IsNullOrEmpty(reference.VerbURI) || string.IsNullOrEmpty(reference.Subject.ID) || string.IsNullOrEmpty(reference.Object.ID))
            {
                notes.Add(new ManifestNote(NoteSeverity.Error, IncompleteReference, $"Reference '{Describe(reference)}' has an empty subject, verb or object.", Describe(reference)));
                continue;
            }

            var subject = Resolve(reference.Subject, manifest, notes, reference);
            var obj = Resolve(reference.Object, manifest, notes, reference);
            if (subject is null || obj is null)
            {
                continue;
            }

            var verb = ManifestIris.Term(reference.VerbURI);
            var subjectNode = Iri(graph, subject.Value.Iri);
            if (verb == belongsToClass)
            {
                graph.Assert(subjectNode, rdfType, Iri(graph, obj.Value.Iri));
            }
            else if (obj.Value.IsVocabularyClass)
            {
                // "subject verb [ a class ]", one blank node per subject, verb and class (Appendix C.3).
                var key = (subject.Value.Iri, verb, obj.Value.Iri);
                if (!blankNodes.TryGetValue(key, out var blank))
                {
                    blank = graph.CreateBlankNode();
                    blankNodes[key] = blank;
                    graph.Assert(blank, rdfType, Iri(graph, obj.Value.Iri));
                }

                graph.Assert(subjectNode, Iri(graph, verb), blank);
            }
            else
            {
                graph.Assert(subjectNode, Iri(graph, verb), Iri(graph, obj.Value.Iri));
            }

            references++;
        }

        return new ManifestGraph(graph, ManifestIris.Provider(provider), manifest.InjectedNodes.Count, manifest.ProvidedVariables.Count, references, blankNodes.Count, notes);
    }

    /// <summary>
    /// Resolves a node identifier to a canonical IRI: an alias of the manifest, a full namespace, or a bare
    /// vocabulary name. Returns null, with a note, when the reference cannot be kept.
    /// </summary>
    private static (string Iri, bool IsVocabularyClass)? Resolve(NodeIdentifier identifier, Blackboard.Contracts.Manifests.Manifest manifest, List<ManifestNote> notes, InjectedReference reference)
    {
        var info = manifest.InjectionInformation;
        var provider = manifest.Provider.Name;
        var ns = identifier.NameSpace ?? "";
        var id = identifier.ID;

        if (ns == info.InjectedNodesNamespaceAlias)
        {
            return (ManifestIris.Node(provider, id), false);
        }

        if (ns == info.ProvidedVariablesNamespaceAlias)
        {
            return (ManifestIris.ProvidedVariable(provider, id), false);
        }

        if (ns == info.InjectedVocabularyNamespaceAlias)
        {
            return (ManifestIris.InjectedTerm(provider, id), false);
        }

        if (ns == info.InjectedVariablesNamespaceAlias)
        {
            notes.Add(new ManifestNote(NoteSeverity.Warning, ReferenceToInjectedVariable,
                $"Reference '{Describe(reference)}' names injected variable '{id}', which is not supported; the reference was dropped.", Describe(reference)));
            return null;
        }

        if (ManifestIris.IsAbsolute(ns))
        {
            var canonicalNs = Namespaces.ToCanonical(ns);
            var isRoot = canonicalNs == Namespaces.Canonical;
            if (ManifestIris.IsAbsolute(id))
            {
                return (Namespaces.ToCanonical(id), isRoot);
            }

            var separator = ns.EndsWith('/') || ns.EndsWith('#') ? "" : "/";
            return (canonicalNs + separator + id, isRoot);
        }

        if (string.IsNullOrEmpty(ns))
        {
            // No namespace: a bare vocabulary name or a full IRI in the id.
            return (ManifestIris.Term(id), !ManifestIris.IsAbsolute(id) || Namespaces.ToCanonical(id).StartsWith(Namespaces.Canonical, StringComparison.Ordinal) && !Namespaces.ToCanonical(id)[Namespaces.Canonical.Length..].Contains('/'));
        }

        notes.Add(new ManifestNote(NoteSeverity.Error, UnknownNamespaceAlias,
            $"Reference '{Describe(reference)}' uses namespace alias '{ns}', which the manifest does not declare; the reference was dropped.", Describe(reference)));
        return null;
    }

    private static IUriNode Iri(Graph graph, string iri) => graph.CreateUriNode(UriFactory.Create(iri));

    private static string Describe(InjectedReference reference) =>
        $"{reference.Subject.NameSpace}:{reference.Subject.ID} {reference.VerbURI} {reference.Object.NameSpace}:{reference.Object.ID}";
}
