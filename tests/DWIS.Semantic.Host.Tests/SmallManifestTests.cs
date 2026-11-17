using System.Net;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;
using static DWIS.Semantic.Host.Tests.HostClient;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// One big and many small (M3.F, Tests #14): the owner's standard scenario is many small manifests of about
/// 10 variables, not one big one. Three parts cut from the SmartConnect manifest (<c>Fixtures/manifests/small</c>,
/// made by <c>tools/split/split-manifest.py</c> of the Tests repository) go live next to the big one: each whole,
/// each with its own graphs and inferences under its own provider, the F.3 queries still answering once.
/// </summary>
[Collection(SharedHost.Name)]
public sealed class SmallManifestTests
{
    private static readonly string[] SmallParts =
    [
        "small/smartconnect-01.manifest.json",
        "small/smartconnect-02.manifest.json",
        "small/smartconnect-03.manifest.json",
    ];

    private static readonly ManifestId SmartConnect = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
    private readonly HostFixture _host;

    public SmallManifestTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task One_big_and_three_small_manifests_live_side_by_side()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        var live = new List<ManifestId>();
        try
        {
            var (bigStatus, big) = await PostAsync(client, "smartconnect.manifest.json");
            bigStatus.Should().Be(HttpStatusCode.OK);
            big.Accepted.Should().BeTrue();
            live.Add(SmartConnect);

            // 1. Every small part goes live whole, with its own inferred graph, far smaller than the big one.
            foreach (var part in SmallParts)
            {
                var id = ManifestId.Of(ManifestJson.Parse(await File.ReadAllTextAsync(HostFixture.ManifestPath(part))));
                var (status, report) = await PostAsync(client, part);
                status.Should().Be(HttpStatusCode.OK, part);
                live.Add(id);
                report.ManifestId.Should().Be(id.Value);
                report.Accepted.Should().BeTrue(part);
                report.Counts.Errors.Should().Be(0, part);
                report.Live.Should().Be(new LiveParts(true, true), part);
                report.Statistics.Asserted.Should().BeInRange(50, big.Statistics.Asserted / 5, "a part is about a tenth of the big manifest");
                report.Statistics.Inferred.Should().BeInRange(1, big.Statistics.Inferred / 5, part);
                (await AskAsync(client, $"ASK {{ GRAPH <{id.InferredGraph}> {{ ?s ?p ?o }} GRAPH <{id.AssertedGraph}> {{ ?s ?p ?o }} }}")).Should().BeFalse("nothing is both asserted and inferred");
                // Ten provided variables, each with a data point of this part, under this part's provider.
                var dataPoints = await SelectAsync(client, $"PREFIX dwis: <http://d-wis.org/> SELECT (COUNT(DISTINCT ?dp) AS ?n) WHERE {{ GRAPH <{id.AssertedGraph}> {{ ?dp dwis:HasDynamicValue ?signal }} }}");
                dataPoints.Should().ContainSingle().Which.Should().Be("10", part);
                var foreign = await AskAsync(client, $"PREFIX dwis: <http://d-wis.org/> ASK {{ GRAPH <{id.AssertedGraph}> {{ ?s ?p ?o . FILTER (isIRI(?s) && !STRSTARTS(STR(?s), \"http://d-wis.org/{id.Provider}/\")) }} }}");
                foreign.Should().BeFalse("every subject of a part is under its own provider, so parts never share a node");
            }

            // 2. All four are listed, each once.
            var list = await ListAsync(client);
            foreach (var id in live)
            {
                list.Should().ContainSingle(e => e.GetProperty("id").GetProperty("value").GetString() == id.Value);
            }

            // 3. The F.3 queries still answer once: the standpipe pressure is in the big manifest and in part 07, not in parts 01 to 03.
            var byClass = await SelectAsync(client, SppByClass);
            byClass.Should().ContainSingle().Which.Should().Be("http://d-wis.org/SmartConnect/Variables/SmartConnect.mainwellCirculationCommon_pressureStandPipe");
            (await SelectAsync(client, SppByQuantityAndPlace)).Should().Equal(byClass);
            (await AskAsync(client, LegacyIriLeft)).Should().BeFalse();

            // 4. Removing a part leaves the others and the big one untouched.
            await RemoveAsync(client, live[1]);
            live.RemoveAt(1);
            (await ListAsync(client)).Should().HaveCount(c => c >= live.Count)
                .And.NotContain(e => e.GetProperty("id").GetProperty("value").GetString() == "SmartConnect-01/smartconnect-rig-signals-part-01");
            (await SelectAsync(client, SppByClass)).Should().Equal(byClass);
            foreach (var id in live.Skip(1))
            {
                (await AskAsync(client, $"ASK {{ GRAPH <{id.AssertedGraph}> {{ ?s ?p ?o }} }}")).Should().BeTrue(id.Value);
            }
        }
        finally
        {
            foreach (var id in live)
            {
                using var cleanup = await client.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
            }
        }
    }
}
