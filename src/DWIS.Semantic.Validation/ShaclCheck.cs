using DWIS.Blackboard.Contracts.Reports;
using VDS.RDF;

namespace DWIS.Semantic.Validation;

/// <summary>
/// SHACL on the manifest graph plus the vocabulary and the reference individuals, with dotNetRDF
/// (decision D-29). The vocabulary is part of the data graph so that <c>sh:targetClass</c> and
/// <c>sh:class</c> see subclasses; the reference individuals so that units and quantities have types.
/// </summary>
public static class ShaclCheck
{
    private const string Shacl = "http://www.w3.org/ns/shacl#";

    /// <summary>Validates and maps every result to a finding with the severity the shape declares.</summary>
    public static IReadOnlyList<Finding> Run(IGraph manifestGraph, VocabularyModel vocabulary)
    {
        ArgumentNullException.ThrowIfNull(manifestGraph);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (vocabulary.Shapes is null)
        {
            return [];
        }

        var data = new Graph();
        data.Merge(vocabulary.Graph);
        data.Merge(vocabulary.ReferenceData);
        data.Merge(manifestGraph);

        // Only the manifest's own nodes are reported: the vocabulary and the reference data are in the data
        // graph for the class hierarchy and the types, not to be validated here.
        var manifestNodes = new HashSet<string>(manifestGraph.Triples.SubjectNodes.Select(PreprocessingPass.Name), StringComparer.Ordinal);

        var report = vocabulary.Shapes.Validate(data);
        var findings = new List<Finding>();
        foreach (var result in report.Results)
        {
            if (result.FocusNode is null || !manifestNodes.Contains(Text(result.FocusNode)))
            {
                continue;
            }

            var source = NamedShape(result.SourceShape, vocabulary.ShapesGraph);
            findings.Add(new Finding
            {
                Stage = Stage.Preprocessing,
                Severity = SeverityOf(result.Severity),
                Code = FindingCodes.Shacl,
                Message = result.Message?.ToString() is { Length: > 0 } message ? Strip(message) : $"Shape {PreprocessingPass.Local(source)} is not satisfied.",
                Focus = Text(result.FocusNode),
                Path = result.ResultPath is null ? null : Text(result.ResultPath),
                Value = result.ResultValue is null ? null : Text(result.ResultValue),
                Source = source,
            });
        }

        return findings;
    }

    // A property shape is usually a blank node; name the node shape that owns it (sh:property) so that the
    // source is stable across runs and readable in the report.
    private static string NamedShape(INode? sourceShape, Graph? shapes)
    {
        if (sourceShape is null)
        {
            return "";
        }

        if (sourceShape.NodeType != NodeType.Blank || shapes is null)
        {
            return Text(sourceShape);
        }

        var property = shapes.CreateUriNode(UriFactory.Create(Shacl + "property"));
        var owner = shapes.GetTriplesWithPredicateObject(property, shapes.GetBlankNode(((IBlankNode)sourceShape).InternalID) ?? sourceShape)
            .Select(t => t.Subject).FirstOrDefault(s => s.NodeType == NodeType.Uri);
        return owner is null ? Text(sourceShape) : Text(owner);
    }

    private static Severity SeverityOf(INode? severity) => Text(severity) switch
    {
        Shacl + "Warning" => Severity.Warning,
        Shacl + "Info" => Severity.Info,
        _ => Severity.Error,
    };

    private static string Text(INode? node) => node is null ? "" : PreprocessingPass.Name(node);

    // A literal with a language tag prints as "text"@EN; keep the text.
    private static string Strip(string message)
    {
        var at = message.LastIndexOf("\"@", StringComparison.Ordinal);
        if (message.StartsWith('"') && at > 0)
        {
            return message[1..at];
        }

        return message.Trim('"');
    }
}
