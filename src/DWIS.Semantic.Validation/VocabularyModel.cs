using DWIS.Blackboard.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Shacl;

namespace DWIS.Semantic.Validation;

/// <summary>
/// What the pre-processing pass knows about the vocabulary, computed once per vocabulary version
/// (Appendix C.4): the classes, verbs and attributes, the subclass and sub-property closures,
/// domains and ranges, functional properties, disjoint classes, the SHACL shapes, and the reference
/// individuals (units and quantities) with their types. Every IRI is canonical.
/// </summary>
public sealed class VocabularyModel
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    private const string Owl = "http://www.w3.org/2002/07/owl#";

    private readonly Dictionary<string, HashSet<string>> _superClasses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _superProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _domains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _ranges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _unionMembers = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string)> _disjoint = [];
    private readonly Dictionary<string, HashSet<string>> _individualTypes = new(StringComparer.Ordinal);

    private VocabularyModel(string version, Graph vocabulary, Graph referenceData, Graph? shapes)
    {
        Version = version;
        Graph = vocabulary;
        ReferenceData = referenceData;
        ShapesGraph = shapes;
        Shapes = shapes is null ? null : new ShapesGraph(shapes);
        Index(vocabulary);
        IndexIndividuals(referenceData);
    }

    /// <summary>The SHACL shapes as triples, or null when none were loaded.</summary>
    public Graph? ShapesGraph { get; }

    /// <summary>The vocabulary version, as reported.</summary>
    public string Version { get; }

    /// <summary>The vocabulary triples (classes, properties, axioms), canonical IRIs.</summary>
    public Graph Graph { get; }

    /// <summary>The reference individuals (units and quantities) with their relations and attributes.</summary>
    public Graph ReferenceData { get; }

    /// <summary>The SHACL shapes, or null when none were loaded.</summary>
    public ShapesGraph? Shapes { get; }

    /// <summary>Known classes.</summary>
    public IReadOnlySet<string> Classes { get; private set; } = new HashSet<string>();

    /// <summary>Known object properties (verbs).</summary>
    public IReadOnlySet<string> ObjectProperties { get; private set; } = new HashSet<string>();

    /// <summary>Known datatype properties (attributes).</summary>
    public IReadOnlySet<string> DatatypeProperties { get; private set; } = new HashSet<string>();

    /// <summary>Properties declared functional.</summary>
    public IReadOnlySet<string> FunctionalProperties { get; private set; } = new HashSet<string>();

    /// <summary>
    /// Loads vocabulary files, optional shape files and optional reference data, translating the legacy
    /// namespace on the way. Several shape files are merged: the vocabulary project's and the project's own.
    /// </summary>
    public static VocabularyModel Load(string version, IEnumerable<string> vocabularyFiles, IEnumerable<string>? shapesFiles = null, IEnumerable<string>? referenceDataFiles = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(vocabularyFiles);

        var vocabulary = new Graph();
        foreach (var file in vocabularyFiles)
        {
            ParseInto(vocabulary, file);
        }

        var reference = new Graph();
        foreach (var file in referenceDataFiles ?? [])
        {
            ParseInto(reference, file);
        }

        Graph? shapes = null;
        foreach (var file in shapesFiles ?? [])
        {
            shapes ??= new Graph();
            ParseInto(shapes, file);
        }

        return new VocabularyModel(version, vocabulary, reference, shapes);
    }

    /// <summary>Builds the model from graphs already in memory (canonical IRIs expected).</summary>
    public static VocabularyModel FromGraphs(string version, Graph vocabulary, Graph? shapes = null, Graph? referenceData = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(vocabulary);
        return new VocabularyModel(version, vocabulary, referenceData ?? new Graph(), shapes);
    }

    /// <summary>True when the class is known.</summary>
    public bool IsClass(string iri) => Classes.Contains(iri);

    /// <summary>True when the property is known, as verb or attribute.</summary>
    public bool IsProperty(string iri) => ObjectProperties.Contains(iri) || DatatypeProperties.Contains(iri);

    /// <summary>The class and all its superclasses (transitive), or the class alone when unknown.</summary>
    public IReadOnlySet<string> SuperClassesOf(string classIri) => _superClasses.TryGetValue(classIri, out var set) ? set : new HashSet<string> { classIri };

    /// <summary>The declared domains of a property, own and inherited from super-properties.</summary>
    public IReadOnlySet<string> DomainsOf(string property) => Inherited(property, _domains);

    /// <summary>The declared ranges of a property, own and inherited from super-properties.</summary>
    public IReadOnlySet<string> RangesOf(string property) => Inherited(property, _ranges);

    /// <summary>The types of a reference individual (a unit or quantity), or null when unknown.</summary>
    public IReadOnlySet<string>? TypesOfIndividual(string iri) => _individualTypes.TryGetValue(iri, out var types) ? types : null;

    /// <summary>True when some asserted type (with its superclasses) satisfies the class, union members included.</summary>
    public bool Satisfies(IEnumerable<string> assertedTypes, string classIri)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in assertedTypes)
        {
            expanded.UnionWith(SuperClassesOf(type));
        }

        return Satisfies(expanded, classIri, 0);
    }

    /// <summary>The disjoint pair among the closures of the asserted types, or null.</summary>
    public (string A, string B)? DisjointPair(IEnumerable<string> assertedTypes)
    {
        var expanded = new List<(string Asserted, string Super)>();
        foreach (var type in assertedTypes)
        {
            foreach (var super in SuperClassesOf(type))
            {
                expanded.Add((type, super));
            }
        }

        foreach (var (assertedA, a) in expanded)
        {
            foreach (var (assertedB, b) in expanded)
            {
                if (assertedA != assertedB && (_disjoint.Contains((a, b)) || _disjoint.Contains((b, a))))
                {
                    return (assertedA, assertedB);
                }
            }
        }

        return null;
    }

    private bool Satisfies(HashSet<string> expandedTypes, string classIri, int depth)
    {
        if (expandedTypes.Contains(classIri))
        {
            return true;
        }

        if (depth < 3 && _unionMembers.TryGetValue(classIri, out var members))
        {
            return members.Any(m => Satisfies(expandedTypes, m, depth + 1));
        }

        return false;
    }

    private HashSet<string> Inherited(string property, Dictionary<string, HashSet<string>> table)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _superProperties.TryGetValue(property, out var supers) ? supers : [property])
        {
            if (table.TryGetValue(p, out var set))
            {
                result.UnionWith(set);
            }
        }

        return result;
    }

    private static void ParseInto(Graph graph, string file)
    {
        var text = File.ReadAllText(file).Replace(Namespaces.Legacy, Namespaces.Canonical, StringComparison.Ordinal);
        new TurtleParser().Load(graph, new StringReader(text));
    }

    private void Index(Graph g)
    {
        var rdfType = g.CreateUriNode(UriFactory.Create(Rdf + "type"));
        var subClassOf = g.CreateUriNode(UriFactory.Create(Rdfs + "subClassOf"));
        var subPropertyOf = g.CreateUriNode(UriFactory.Create(Rdfs + "subPropertyOf"));
        var domain = g.CreateUriNode(UriFactory.Create(Rdfs + "domain"));
        var range = g.CreateUriNode(UriFactory.Create(Rdfs + "range"));
        var unionOf = g.CreateUriNode(UriFactory.Create(Owl + "unionOf"));
        var disjointWith = g.CreateUriNode(UriFactory.Create(Owl + "disjointWith"));
        var members = g.CreateUriNode(UriFactory.Create(Owl + "members"));

        var classes = new HashSet<string>(StringComparer.Ordinal);
        var objectProperties = new HashSet<string>(StringComparer.Ordinal);
        var datatypeProperties = new HashSet<string>(StringComparer.Ordinal);
        var functional = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in g.GetTriplesWithPredicate(rdfType))
        {
            if (t.Subject is not IUriNode subject || t.Object is not IUriNode type)
            {
                continue;
            }

            var s = subject.Uri.AbsoluteUri;
            switch (type.Uri.AbsoluteUri)
            {
                case Owl + "Class":
                case Rdfs + "Class":
                    classes.Add(s);
                    break;
                case Owl + "ObjectProperty":
                    objectProperties.Add(s);
                    break;
                case Owl + "DatatypeProperty":
                case Owl + "AnnotationProperty":
                    datatypeProperties.Add(s);
                    break;
                case Owl + "FunctionalProperty":
                    functional.Add(s);
                    break;
                case Rdf + "Property":
                    objectProperties.Add(s);
                    break;
                default:
                    break;
            }
        }

        // A property typed functional only (as in the Units definitions) is still a property.
        foreach (var f in functional)
        {
            if (!objectProperties.Contains(f) && !datatypeProperties.Contains(f))
            {
                objectProperties.Add(f);
            }
        }

        Classes = classes;
        ObjectProperties = objectProperties;
        DatatypeProperties = datatypeProperties;
        FunctionalProperties = functional;

        var directSuperClasses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in g.GetTriplesWithPredicate(subClassOf))
        {
            if (t.Subject is IUriNode s && t.Object is IUriNode o)
            {
                Add(directSuperClasses, s.Uri.AbsoluteUri, o.Uri.AbsoluteUri);
            }
        }

        foreach (var c in classes)
        {
            _superClasses[c] = Closure(c, directSuperClasses);
        }

        var directSuperProperties = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in g.GetTriplesWithPredicate(subPropertyOf))
        {
            if (t.Subject is IUriNode s && t.Object is IUriNode o)
            {
                Add(directSuperProperties, s.Uri.AbsoluteUri, o.Uri.AbsoluteUri);
            }
        }

        foreach (var p in objectProperties.Concat(datatypeProperties))
        {
            _superProperties[p] = Closure(p, directSuperProperties);
        }

        foreach (var t in g.GetTriplesWithPredicate(domain))
        {
            if (t.Subject is IUriNode s)
            {
                Add(_domains, s.Uri.AbsoluteUri, Name(t.Object));
            }
        }

        foreach (var t in g.GetTriplesWithPredicate(range))
        {
            if (t.Subject is IUriNode s)
            {
                Add(_ranges, s.Uri.AbsoluteUri, Name(t.Object));
            }
        }

        foreach (var t in g.GetTriplesWithPredicate(unionOf))
        {
            foreach (var member in g.GetListItems(t.Object))
            {
                Add(_unionMembers, Name(t.Subject), Name(member));
            }
        }

        foreach (var t in g.GetTriplesWithPredicate(disjointWith))
        {
            if (t.Subject is IUriNode s && t.Object is IUriNode o)
            {
                _disjoint.Add((s.Uri.AbsoluteUri, o.Uri.AbsoluteUri));
            }
        }

        foreach (var t in g.GetTriplesWithPredicate(members))
        {
            var items = g.GetListItems(t.Object).OfType<IUriNode>().Select(n => n.Uri.AbsoluteUri).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                for (var j = i + 1; j < items.Count; j++)
                {
                    _disjoint.Add((items[i], items[j]));
                }
            }
        }
    }

    private void IndexIndividuals(Graph reference)
    {
        var rdfType = reference.CreateUriNode(UriFactory.Create(Rdf + "type"));
        foreach (var t in reference.GetTriplesWithPredicate(rdfType))
        {
            if (t.Subject is IUriNode s && t.Object is IUriNode o)
            {
                Add(_individualTypes, s.Uri.AbsoluteUri, o.Uri.AbsoluteUri);
            }
        }
    }

    private static string Name(INode node) => node.NodeType switch
    {
        NodeType.Uri => ((IUriNode)node).Uri.AbsoluteUri,
        NodeType.Blank => "_:" + ((IBlankNode)node).InternalID,
        _ => node.ToString() ?? "",
    };

    private static void Add(Dictionary<string, HashSet<string>> table, string key, string value)
    {
        if (!table.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            table[key] = set;
        }

        set.Add(value);
    }

    private static HashSet<string> Closure(string start, Dictionary<string, HashSet<string>> direct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!direct.TryGetValue(current, out var parents))
            {
                continue;
            }

            foreach (var parent in parents)
            {
                if (result.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return result;
    }
}
