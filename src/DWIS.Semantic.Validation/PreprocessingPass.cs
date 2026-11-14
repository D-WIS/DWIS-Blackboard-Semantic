using System.Diagnostics;
using DWIS.Blackboard.Contracts;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Manifest;
using VDS.RDF;

namespace DWIS.Semantic.Validation;

/// <summary>What the pass can ask about the rest of the store (Appendix C.4: "exists in neither this manifest nor the store").</summary>
public sealed class PreprocessingOptions
{
    /// <summary>Tells whether an IRI exists in the store, for references that leave the manifest. Default: nothing exists.</summary>
    public Func<string, bool> ExistsInStore { get; init; } = _ => false;

    /// <summary>Gives the types of an IRI in the store, or null when unknown. Default: unknown.</summary>
    public Func<string, IReadOnlySet<string>?> TypesInStore { get; init; } = _ => null;

    /// <summary>Run the SHACL shapes when the vocabulary has some. Default: true.</summary>
    public bool RunShacl { get; init; } = true;

    /// <summary>Run the unit against quantity dimension check when the reference data allows it. Default: true.</summary>
    public bool CheckUnitDimensions { get; init; } = true;

    /// <summary>Severity overrides applied to the findings. Default: none.</summary>
    public SeverityPolicy Policy { get; init; } = SeverityPolicy.None;
}

/// <summary>The outcome of the pass.</summary>
/// <param name="Findings">Errors first.</param>
/// <param name="Milliseconds">Time spent.</param>
public sealed record PreprocessingResult(IReadOnlyList<Finding> Findings, long Milliseconds)
{
    /// <summary>True when there is no error (the whole manifest may go live).</summary>
    public bool Accepted => Findings.All(f => f.Severity != Severity.Error);

    /// <summary>The report of Appendix C.9 for this pass alone.</summary>
    public IngestionReport ToReport(string manifestId, string vocabularyVersion) =>
        IngestionReport.From(manifestId, vocabularyVersion, Findings, new IngestionStatistics { PreprocessingMs = Milliseconds });
}

/// <summary>
/// The pre-processing pass of Appendix C.4, in memory, on the manifest graph plus the vocabulary model.
/// Pure: same manifest, same vocabulary, same findings.
/// </summary>
public static class PreprocessingPass
{
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

    /// <summary>Runs every check and returns the findings, errors first.</summary>
    public static PreprocessingResult Run(PreparedManifest prepared, ManifestGraph manifestGraph, VocabularyModel vocabulary, PreprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(manifestGraph);
        ArgumentNullException.ThrowIfNull(vocabulary);
        options ??= new PreprocessingOptions();
        var watch = Stopwatch.StartNew();
        var findings = new List<Finding>();

        findings.AddRange(prepared.Notes.Select(ToFinding));
        findings.AddRange(manifestGraph.Notes.Select(ToFinding));

        var graph = manifestGraph.Graph;
        var provider = prepared.Completed.Provider.Name;
        var injectedTermPrefix = ManifestIris.InjectedTerm(provider, "");
        var types = AssertedTypes(graph);
        var subjects = new HashSet<string>(graph.Triples.SubjectNodes.OfType<IUriNode>().Select(n => n.Uri.AbsoluteUri), StringComparer.Ordinal);

        CheckTermsDomainsRangesAndReferences(graph, types, subjects, vocabulary, options, injectedTermPrefix, findings);
        CheckFunctional(graph, vocabulary, findings);
        CheckDisjointness(types, vocabulary, findings);
        if (options.RunShacl && vocabulary.Shapes is not null)
        {
            findings.AddRange(ShaclCheck.Run(graph, vocabulary));
        }

        if (options.CheckUnitDimensions)
        {
            findings.AddRange(UnitQuantityCheck.Run(graph, vocabulary));
        }

        var belongsToClass = prepared.Completed.InjectedReferences.Count(r => ManifestIris.Term(r.VerbURI) == Manifest.Vocabulary.BelongsToClass);
        if (belongsToClass > 0)
        {
            findings.Add(new Finding
            {
                Severity = Severity.Info,
                Code = FindingCodes.BelongsToClassUsed,
                Message = $"{belongsToClass} references use BelongsToClass, the deprecated form of a class assertion; rdf:type is preferred.",
            });
        }

        watch.Stop();
        return new PreprocessingResult(options.Policy.Apply(findings).OrderByDescending(f => f.Severity).ToList(), watch.ElapsedMilliseconds);
    }

    private static Finding ToFinding(ManifestNote note) => new()
    {
        Stage = Stage.Preprocessing,
        Severity = note.Severity switch { NoteSeverity.Error => Severity.Error, NoteSeverity.Warning => Severity.Warning, _ => Severity.Info },
        Code = note.Code,
        Message = note.Message,
        Focus = note.Subject,
    };

    private static Dictionary<string, HashSet<string>> AssertedTypes(IGraph graph)
    {
        var rdfType = graph.CreateUriNode(UriFactory.Create(RdfType));
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in graph.GetTriplesWithPredicate(rdfType))
        {
            if (t.Object is not IUriNode type)
            {
                continue;
            }

            var subject = Name(t.Subject);
            if (!result.TryGetValue(subject, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[subject] = set;
            }

            set.Add(type.Uri.AbsoluteUri);
        }

        return result;
    }

    private static void CheckTermsDomainsRangesAndReferences(IGraph graph, Dictionary<string, HashSet<string>> types, HashSet<string> subjects, VocabularyModel vocabulary, PreprocessingOptions options, string injectedTermPrefix, List<Finding> findings)
    {
        var reportedUnknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in graph.Triples)
        {
            var subject = Name(t.Subject);
            var predicate = Name(t.Predicate);

            if (predicate == RdfType)
            {
                var type = Name(t.Object);
                if (!vocabulary.IsClass(type) && !type.StartsWith(injectedTermPrefix, StringComparison.Ordinal))
                {
                    findings.Add(new Finding { Code = FindingCodes.UnknownClass, Message = $"Class <{type}> is not in the vocabulary.", Focus = subject, Path = RdfType, Value = type });
                }

                continue;
            }

            if (!predicate.StartsWith(Namespaces.Canonical, StringComparison.Ordinal))
            {
                continue;
            }

            var isLiteral = t.Object is ILiteralNode;
            if (!vocabulary.IsProperty(predicate))
            {
                if (reportedUnknown.Add(subject + " " + predicate))
                {
                    findings.Add(new Finding
                    {
                        Code = isLiteral ? FindingCodes.UnknownAttribute : FindingCodes.UnknownVerb,
                        Message = isLiteral ? $"Attribute <{predicate}> is not in the vocabulary." : $"Verb <{predicate}> is not in the vocabulary.",
                        Focus = subject,
                        Path = predicate,
                    });
                }

                continue;
            }

            // Domain: every declared domain must hold for the subject (RDFS intersects them).
            if (types.TryGetValue(subject, out var subjectTypes))
            {
                foreach (var domain in vocabulary.DomainsOf(predicate))
                {
                    if (!vocabulary.Satisfies(subjectTypes, domain))
                    {
                        findings.Add(new Finding
                        {
                            Code = FindingCodes.DomainMismatch,
                            Message = $"<{subject}> uses <{predicate}> but is not a {Local(domain)} (it is {Describe(subjectTypes)}).",
                            Focus = subject,
                            Path = predicate,
                            Value = domain,
                        });
                    }
                }
            }

            if (isLiteral)
            {
                continue;
            }

            // Object: known in the manifest, in the reference data, in the store, or dangling.
            var obj = Name(t.Object);
            IReadOnlySet<string>? objectTypes = types.TryGetValue(obj, out var inManifest) ? inManifest : null;
            var known = objectTypes is not null || subjects.Contains(obj) || t.Object is IBlankNode;
            if (!known)
            {
                objectTypes = vocabulary.TypesOfIndividual(obj);
                known = objectTypes is not null || vocabulary.IsClass(obj);
            }

            if (!known)
            {
                objectTypes = options.TypesInStore(obj);
                known = objectTypes is not null || options.ExistsInStore(obj);
            }

            if (!known)
            {
                findings.Add(new Finding
                {
                    Code = FindingCodes.DanglingReference,
                    Message = $"<{subject}> refers through <{predicate}> to <{obj}>, which exists neither in the manifest nor in the store.",
                    Focus = subject,
                    Path = predicate,
                    Value = obj,
                });
                continue;
            }

            if (objectTypes is null)
            {
                continue;
            }

            foreach (var range in vocabulary.RangesOf(predicate))
            {
                if (!vocabulary.Satisfies(objectTypes, range))
                {
                    findings.Add(new Finding
                    {
                        Code = FindingCodes.RangeMismatch,
                        Message = $"<{subject}> <{predicate}> <{obj}>: the object is not a {Local(range)} (it is {Describe(objectTypes)}).",
                        Focus = subject,
                        Path = predicate,
                        Value = obj,
                    });
                }
            }
        }
    }

    private static void CheckFunctional(IGraph graph, VocabularyModel vocabulary, List<Finding> findings)
    {
        var values = new Dictionary<(string Subject, string Predicate), HashSet<string>>();
        foreach (var t in graph.Triples)
        {
            var predicate = Name(t.Predicate);
            if (!vocabulary.FunctionalProperties.Contains(predicate))
            {
                continue;
            }

            var key = (Name(t.Subject), predicate);
            if (!values.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                values[key] = set;
            }

            set.Add(Name(t.Object));
        }

        foreach (var ((subject, predicate), set) in values.Where(v => v.Value.Count > 1))
        {
            findings.Add(new Finding
            {
                Code = FindingCodes.FunctionalConflict,
                Message = $"<{subject}> has {set.Count} values for the functional property <{predicate}>.",
                Focus = subject,
                Path = predicate,
                Value = string.Join(", ", set.Order(StringComparer.Ordinal)),
            });
        }
    }

    private static void CheckDisjointness(Dictionary<string, HashSet<string>> types, VocabularyModel vocabulary, List<Finding> findings)
    {
        foreach (var (subject, set) in types.Where(t => t.Value.Count > 1))
        {
            var pair = vocabulary.DisjointPair(set);
            if (pair is not null)
            {
                findings.Add(new Finding
                {
                    Code = FindingCodes.DisjointClasses,
                    Message = $"<{subject}> is asserted to be both a {Local(pair.Value.A)} and a {Local(pair.Value.B)}, which are disjoint.",
                    Focus = subject,
                    Path = RdfType,
                    Value = pair.Value.A + ", " + pair.Value.B,
                });
            }
        }
    }

    // By node type, not by interface: dotNetRDF's SHACL results wrap nodes in a type that implements every
    // node interface and throws on the ones that do not apply.
    internal static string Name(INode node) => node.NodeType switch
    {
        NodeType.Uri => ((IUriNode)node).Uri.AbsoluteUri,
        NodeType.Literal => ((ILiteralNode)node).Value,
        NodeType.Blank => "_:" + ((IBlankNode)node).InternalID,
        _ => node.ToString() ?? "",
    };

    internal static string Local(string iri)
    {
        var cut = Math.Max(iri.LastIndexOf('/'), iri.LastIndexOf('#'));
        return cut >= 0 && cut < iri.Length - 1 ? iri[(cut + 1)..] : iri;
    }

    private static string Describe(IEnumerable<string> types) => string.Join(", ", types.Select(Local).Order(StringComparer.Ordinal));
}
