using System.Collections.Immutable;
using VDS.RDF;
using VDS.RDF.Query;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>
/// One row of a registered query's answer: one text per variable, in the order of <see cref="QueryRows.Variables"/>.
/// IRIs are their canonical text, literals their lexical value, blank nodes <c>_:</c> and their label, an unbound
/// variable null. Two rows are equal when their texts are: that is what a diff compares (specification 3.5).
/// </summary>
public sealed class QueryRow : IEquatable<QueryRow>
{
    /// <summary>Creates a row from its values.</summary>
    public QueryRow(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = [.. values];
    }

    /// <summary>Creates a row from its values.</summary>
    public QueryRow(params string?[] values)
        : this((IEnumerable<string?>)values)
    {
    }

    /// <summary>The values, one per variable.</summary>
    public ImmutableArray<string?> Values { get; }

    /// <summary>The value of the variable at an index.</summary>
    public string? this[int index] => Values[index];

    /// <inheritdoc />
    public bool Equals(QueryRow? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as QueryRow);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => string.Join(" | ", Values.Select(v => v ?? "(unbound)"));
}

/// <summary>The answer to a registered query: its variables and its rows (a SELECT, as the store returns it).</summary>
public sealed class QueryRows
{
    /// <summary>Creates an answer.</summary>
    public QueryRows(IEnumerable<string> variables, IEnumerable<QueryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(rows);
        Variables = [.. variables];
        Rows = [.. rows];
    }

    /// <summary>No variables, no rows.</summary>
    public static QueryRows Empty { get; } = new([], []);

    /// <summary>The variables, in the order of the query's projection.</summary>
    public ImmutableArray<string> Variables { get; }

    /// <summary>The rows, in the order the store returned them.</summary>
    public ImmutableArray<QueryRow> Rows { get; }

    /// <summary>The rows of a SELECT answer of the store, as texts (see <see cref="QueryRow"/>).</summary>
    public static QueryRows FromResultSet(SparqlResultSet results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var variables = results.Variables.ToList();
        return new QueryRows(variables, results.Results.Select(r => new QueryRow(variables.Select(v => r.HasBoundValue(v) ? Text(r[v]) : null))));
    }

    private static string Text(INode node) => node switch
    {
        IUriNode uri => uri.Uri.AbsoluteUri,
        ILiteralNode literal => literal.Value,
        IBlankNode blank => "_:" + blank.InternalID,
        _ => node.ToString() ?? "",
    };
}
