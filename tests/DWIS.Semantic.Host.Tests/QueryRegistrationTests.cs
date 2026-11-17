using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Semantic.Ingest;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using static DWIS.Semantic.Host.Tests.HostClient;
using GrpcSemantic = DWIS.Blackboard.Contracts.Grpc.Semantic;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// Query registration on both transports (M3.4; specification 3.4, 3.5; R-QR-2, R-QR-3): the gRPC calls with
/// one <c>WatchQueries</c> stream, and the HTTP routes with server-sent events; diffs after inject, update and
/// remove (T-QR-2); two owners and one release (T-QR-3); a new registration after a loss gets a full snapshot.
/// </summary>
[Collection(SharedHost.Name)]
public sealed class QueryRegistrationTests
{
    private const string Part01 = "small/smartconnect-01.manifest.json";
    private const string Part02 = "small/smartconnect-02.manifest.json";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private readonly HostFixture _host;

    public QueryRegistrationTests(HostFixture host)
    {
        _host = host;
    }

    private static string DataPointsOf(string provider) => $$"""
        PREFIX ddhub: <http://ddhub.no/>
        SELECT ?dp ?signal WHERE { ?dp ddhub:HasDynamicValue ?signal . FILTER (STRSTARTS(STR(?dp), "http://d-wis.org/{{provider}}/")) }
        """;

    [Fact]
    public async Task Registration_over_grpc_with_a_watch_stream_sees_every_change()
    {
        _host.SkipUnlessAvailable();
        using var http = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(http);
        using var channel = GrpcChannel.ForAddress(_host.Factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _host.Factory.Server.CreateHandler() });
        var client = new GrpcSemantic.SemanticClient(channel);
        var json = await File.ReadAllTextAsync(HostFixture.ManifestPath(Part01));
        var id = ManifestId.Of(ManifestJson.Parse(json));
        var query = DataPointsOf("SmartConnect-01");
        using var stop = new CancellationTokenSource();
        var diffs = new List<QueryDiff>();
        try
        {
            // 1. Two sessions register the same text: one query, the snapshot empty on an empty description.
            var first = await client.RegisterQueryAsync(new RegisterRequest { Query = query, Owner = "session 1" });
            first.Variables.Should().Equal("dp", "signal");
            first.Rows.Should().BeEmpty();
            var second = await client.RegisterQueryAsync(new RegisterRequest { Query = query, Owner = "session 2" });
            second.QueryId.Should().Be(first.QueryId);
            (await client.GetVocabularyAsync(new Empty())).RegisteredQueries.Should().Be(1);

            // 2. One stream for the real-time process.
            using var watch = client.WatchQueries(new WatchRequest { Watcher = "test" }, cancellationToken: stop.Token);
            var reading = Task.Run(async () =>
            {
                await foreach (var diff in watch.ResponseStream.ReadAllAsync(stop.Token))
                {
                    lock (diffs)
                    {
                        diffs.Add(diff);
                    }
                }
            });

            // 3. Inject: a diff with the ten data points added, with the query id, canonical IRIs, a time stamp.
            (await PostAsync(http, Part01)).Report.Accepted.Should().BeTrue();
            await WaitForAsync(() => diffs.Count >= 1);
            var added = diffs[0];
            added.QueryId.Should().Be(first.QueryId);
            added.Variables.Should().Equal("dp", "signal");
            added.Added.Should().HaveCount(10).And.OnlyContain(r => r.Values[0].StartsWith("http://d-wis.org/SmartConnect-01/"));
            added.Removed.Should().BeEmpty();
            added.At.ToDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));

            // 4. Update (the same id, one data point moved): a diff with one row removed and one added.
            var original = ManifestJson.Parse(json);
            var moved = original with
            {
                InjectedNodes = [.. original.InjectedNodes, new InjectedNode { TypeDictionaryURI = original.InjectedNodes[0].TypeDictionaryURI, BrowseName = "moved", UniqueName = "moved", DisplayName = "moved" }],
                InjectedReferences = original.InjectedReferences.Select(r =>
                    r.Subject.ID == original.InjectedNodes[0].UniqueName && r.VerbURI.EndsWith("HasDynamicValue", StringComparison.Ordinal) ? r with { Subject = r.Subject with { ID = "moved" } } : r).ToList(),
            };
            using (var put = await http.PutAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative), new StringContent(ManifestJson.Serialize(moved), System.Text.Encoding.UTF8, "application/json")))
            {
                put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
            }

            await WaitForAsync(() => diffs.Count >= 2);
            diffs[1].Added.Should().ContainSingle().Which.Values[0].Should().Be("http://d-wis.org/SmartConnect-01/moved");
            diffs[1].Removed.Should().ContainSingle();

            // 5. Remove: a diff with the ten rows removed.
            await RemoveAsync(http, id);
            await WaitForAsync(() => diffs.Count >= 3);
            diffs[2].Removed.Should().HaveCount(10);
            diffs[2].Added.Should().BeEmpty();

            // 6. Two owners, one release: the query lives until the last owner goes (T-QR-3).
            (await client.UnregisterQueryAsync(new UnregisterRequest { Query = query, Owner = "nobody" })).Released.Should().BeFalse();
            var released = await client.ReleaseOwnerAsync(new OwnerRef { Owner = "session 1" });
            released.QueryIds.Should().BeEmpty("session 2 still owns the query");
            (await client.GetVocabularyAsync(new Empty())).RegisteredQueries.Should().Be(1);
            var last = await client.UnregisterQueryAsync(new UnregisterRequest { Query = query, Owner = "session 2" });
            last.Released.Should().BeTrue();
            last.QueryId.Should().Be(first.QueryId);
            (await client.GetVocabularyAsync(new Empty())).RegisteredQueries.Should().Be(0);

            // 7. Registered again, after the loss of everything: a new entry, a full snapshot (the server side of T-QR-4).
            (await PostAsync(http, Part01)).Report.Accepted.Should().BeTrue();
            var again = await client.RegisterQueryAsync(new RegisterRequest { Query = query, Owner = "session 3" });
            again.QueryId.Should().NotBe(first.QueryId);
            again.Rows.Should().HaveCount(10);
            await client.ReleaseOwnerAsync(new OwnerRef { Owner = "session 3" });

            // 8. A query that is not a SELECT is refused and leaves no entry.
            var ask = async () => await client.RegisterQueryAsync(new RegisterRequest { Query = "ASK { ?s ?p ?o }", Owner = "session 3" });
            (await ask.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
            (await client.GetVocabularyAsync(new Empty())).RegisteredQueries.Should().Be(0);

            // 9. The stream ends when the watcher goes.
            await stop.CancelAsync();
            var ended = async () => await reading;
            (await ended.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Cancelled);
        }
        finally
        {
            await stop.CancelAsync();
            await client.ReleaseOwnerAsync(new OwnerRef { Owner = "session 1" });
            await client.ReleaseOwnerAsync(new OwnerRef { Owner = "session 2" });
            await client.ReleaseOwnerAsync(new OwnerRef { Owner = "session 3" });
            using var cleanup = await http.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        }
    }

    [Fact]
    public async Task Registration_over_http_with_server_sent_events()
    {
        _host.SkipUnlessAvailable();
        using var http = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(http);
        var json = await File.ReadAllTextAsync(HostFixture.ManifestPath(Part02));
        var id = ManifestId.Of(ManifestJson.Parse(json));
        using var stop = new CancellationTokenSource();
        var events = new List<(string Type, JsonElement Data)>();
        int queryId;
        try
        {
            // 1. Registered for the http owner: the id and the full (empty) answer.
            using var registered = await http.PostAsJsonAsync(new Uri("/api/v1/queries", UriKind.Relative), new { query = DataPointsOf("SmartConnect-02") });
            registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());
            using var answer = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
            queryId = answer.RootElement.GetProperty("id").GetInt32();
            answer.RootElement.GetProperty("variables").EnumerateArray().Select(v => v.GetString()).Should().Equal("dp", "signal");
            answer.RootElement.GetProperty("rows").GetArrayLength().Should().Be(0);

            // 2. The events stream: the snapshot first.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/queries/{queryId}/events", UriKind.Relative));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
            var reading = Task.Run(async () =>
            {
                using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
                using var reader = new StreamReader(stream);
                string? type = null;
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        type = line[6..].Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        lock (events)
                        {
                            events.Add((type ?? "message", JsonDocument.Parse(line[5..].Trim()).RootElement.Clone()));
                        }

                        type = null;
                    }
                }
            });
            await WaitForAsync(() => events.Count >= 1);
            events[0].Type.Should().Be("snapshot");
            events[0].Data.GetProperty("id").GetInt32().Should().Be(queryId);
            events[0].Data.GetProperty("rows").GetArrayLength().Should().Be(0);

            // 3. Inject: a diff event with the ten rows added.
            (await PostAsync(http, Part02)).Report.Accepted.Should().BeTrue();
            await WaitForAsync(() => events.Count >= 2);
            events[1].Type.Should().Be("diff");
            events[1].Data.GetProperty("added").GetArrayLength().Should().Be(10);
            events[1].Data.GetProperty("removed").GetArrayLength().Should().Be(0);

            // 4. DELETE releases the http owner; the stream still owns the query, so the next change still arrives.
            using (var deleted = await http.DeleteAsync(new Uri($"/api/v1/queries/{queryId}", UriKind.Relative)))
            {
                deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            }

            using (var deletedAgain = await http.DeleteAsync(new Uri($"/api/v1/queries/{queryId}", UriKind.Relative)))
            {
                deletedAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }

            await RemoveAsync(http, id);
            await WaitForAsync(() => events.Count >= 3);
            events[2].Type.Should().Be("diff");
            events[2].Data.GetProperty("removed").GetArrayLength().Should().Be(10);

            // 5. The stream closes: its ownership goes with it, and the query is gone.
            await stop.CancelAsync();
            await WaitForAsync(() => _host.Factory.Services.GetService(typeof(QueryRegistration.QueryManager)) is QueryRegistration.QueryManager m && m.TextOf(queryId) is null);

            // 6. An unknown id has no stream.
            using var missing = await http.GetAsync(new Uri($"/api/v1/queries/{queryId}/events", UriKind.Relative));
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var noQuery = await http.PostAsJsonAsync(new Uri("/api/v1/queries", UriKind.Relative), new { query = "" });
            noQuery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await stop.CancelAsync();
            using var cleanup = await http.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (!condition())
        {
            (DateTimeOffset.UtcNow < deadline).Should().BeTrue("the expected event should arrive within a minute");
            await Task.Delay(100);
        }
    }
}
