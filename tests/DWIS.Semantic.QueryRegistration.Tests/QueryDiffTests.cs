namespace DWIS.Semantic.QueryRegistration.Tests;

/// <summary>Rows are compared by value, in any order; a diff from nothing is a full snapshot.</summary>
public sealed class QueryDiffTests
{
    [Fact]
    public void Rows_are_equal_by_value_and_order_does_not_matter()
    {
        var before = new QueryRows(["s", "o"], [new QueryRow("urn:a", "1"), new QueryRow("urn:b", null)]);
        var after = new QueryRows(["s", "o"], [new QueryRow("urn:b", null), new QueryRow("urn:c", "3"), new QueryRow("urn:a", "1")]);
        var diff = QueryDiff.Between(7, before, after);
        diff.QueryId.Should().Be(7);
        diff.Variables.Should().Equal("s", "o");
        diff.Added.Should().ContainSingle().Which.Should().Be(new QueryRow("urn:c", "3"));
        diff.Removed.Should().BeEmpty();
        QueryDiff.Between(7, after, after).IsEmpty.Should().BeTrue();
        QueryDiff.Between(7, after, before).Removed.Should().ContainSingle().Which[0].Should().Be("urn:c");
    }

    [Fact]
    public void A_snapshot_is_a_diff_from_nothing_without_duplicate_rows()
    {
        var rows = new QueryRows(["s"], [new QueryRow("urn:a"), new QueryRow("urn:a"), new QueryRow("urn:b")]);
        var snapshot = QueryDiff.Between(1, null, rows);
        snapshot.Added.Should().HaveCount(2);
        snapshot.Removed.Should().BeEmpty();
        snapshot.IsEmpty.Should().BeFalse();
        QueryDiff.Between(1, null, QueryRows.Empty).IsEmpty.Should().BeTrue();
    }
}
