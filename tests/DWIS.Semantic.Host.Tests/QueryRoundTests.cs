using System.Net;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Semantic.Ingest;
using DWIS.Semantic.QueryRegistration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static DWIS.Semantic.Host.Tests.HostClient;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// The registered queries against the real store (M3.1): a query registered on the host's <see cref="QueryManager"/>
/// gets a snapshot from Fuseki, and every change to the live description (inject, remove) runs a round whose
/// diffs reach the sinks (R-QR-2, the semantic side of T-QR-2).
/// </summary>
[Collection(SharedHost.Name)]
public sealed class QueryRoundTests
{
    private const string Part = "small/smartconnect-01.manifest.json";
    private const string DataPointsOfPart01 = """
        PREFIX dwis: <http://d-wis.org/>
        SELECT ?dp ?signal WHERE { ?dp dwis:HasDynamicValue ?signal . FILTER (STRSTARTS(STR(?dp), "http://d-wis.org/SmartConnect-01/")) }
        """;

    private readonly HostFixture _host;

    public QueryRoundTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task A_registered_query_gets_a_snapshot_and_a_diff_after_every_change()
    {
        _host.SkipUnlessAvailable();
        var sink = new RecordingSink();
        using var factory = _host.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IQueryDiffSink>(sink)));
        using var client = factory.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        var manager = factory.Services.GetRequiredService<QueryManager>();
        var id = ManifestId.Of(ManifestJson.Parse(await File.ReadAllTextAsync(HostFixture.ManifestPath(Part), TestContext.Current.CancellationToken)));
        try
        {
            // 1. Registered on an empty description: the snapshot has the variables and no row.
            var queryId = manager.Register(DataPointsOfPart01, "test session");
            var snapshot = (await manager.SnapshotAsync(queryId, TestContext.Current.CancellationToken))!;
            snapshot.Variables.Should().Equal("dp", "signal");
            snapshot.Added.Should().BeEmpty();

            // 2. A commit runs a round: the ten data points of the part are added.
            var (status, report) = await PostAsync(client, Part);
            status.Should().Be(HttpStatusCode.OK);
            report.Accepted.Should().BeTrue();
            var added = sink.Diffs.Should().ContainSingle().Which;
            added.QueryId.Should().Be(queryId);
            added.Added.Should().HaveCount(10);
            added.Removed.Should().BeEmpty();
            added.Added.Should().OnlyContain(r => r[0]!.StartsWith("http://d-wis.org/SmartConnect-01/", StringComparison.Ordinal) && r[1]!.Contains("/Variables/", StringComparison.Ordinal));

            // 3. The same manifest again writes nothing and runs no round; a new subscriber still gets the full answer.
            (await PostAsync(client, Part)).Report.Should().BeEquivalentTo(report);
            sink.Diffs.Should().HaveCount(1);
            (await manager.SnapshotAsync(queryId, TestContext.Current.CancellationToken))!.Added.Should().HaveCount(10);

            // 4. A remove runs a round: the ten rows are removed.
            await RemoveAsync(client, id);
            sink.Diffs.Should().HaveCount(2);
            sink.Diffs[1].Removed.Should().HaveCount(10);
            sink.Diffs[1].Added.Should().BeEmpty();

            // 5. The session closes: its query goes, and the next change runs no round for it.
            manager.ReleaseOwner("test session").Should().Equal(queryId);
            manager.Count.Should().Be(0);
        }
        finally
        {
            manager.ReleaseOwner("test session");
            using var cleanup = await client.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative), TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingSink : IQueryDiffSink
    {
        public List<QueryDiff> Diffs { get; } = [];

        public Task PublishAsync(QueryDiff diff, CancellationToken cancellationToken)
        {
            lock (Diffs)
            {
                Diffs.Add(diff);
            }

            return Task.CompletedTask;
        }
    }
}
