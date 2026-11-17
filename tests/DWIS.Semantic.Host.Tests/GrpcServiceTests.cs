using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.Blackboard.Contracts.Reports;
using DWIS.Semantic.Ingest;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static DWIS.Semantic.Host.Tests.HostClient;
using GrpcSemantic = DWIS.Blackboard.Contracts.Grpc.Semantic;
using GrpcVariableEndpoint = DWIS.Blackboard.Contracts.Grpc.VariableEndpoint;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// The gRPC service of specification 3.3 through the generated client (M3.3): the two-phase injection over the
/// staging table with its endpoints, the list for the rebuild, the query and resources facades, the vocabulary,
/// and the staging time limit.
/// </summary>
[Collection(SharedHost.Name)]
public sealed class GrpcServiceTests
{
    private const string Part02 = "small/smartconnect-02.manifest.json";
    private const string Part03 = "small/smartconnect-03.manifest.json";
    private readonly HostFixture _host;

    public GrpcServiceTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task Prepare_commit_with_endpoints_list_query_resources_and_remove()
    {
        _host.SkipUnlessAvailable();
        using var http = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(http);
        using var channel = Channel(_host.Factory);
        var client = new GrpcSemantic.SemanticClient(channel);
        var json = await File.ReadAllTextAsync(HostFixture.ManifestPath(Part02));
        var id = ManifestId.Of(ManifestJson.Parse(json));
        try
        {
            // 0. Ready.
            var vocabulary = await client.GetVocabularyAsync(new Empty());
            vocabulary.Loaded.Should().BeTrue();
            vocabulary.Version.Should().Be(_host.Version);

            // 1. Prepare: accepted, nothing live yet, the variables to create with their validity flags.
            var prepared = await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = json, Origin = "test" });
            prepared.Manifest.Provider.Should().Be("SmartConnect-02");
            prepared.Manifest.Name.Should().Be(id.Name);
            prepared.Accepted.Should().BeTrue();
            prepared.AlreadyLive.Should().BeFalse();
            prepared.ContentHash.Should().StartWith("sha256:");
            prepared.Variables.Should().HaveCount(20);
            prepared.Variables.Where(v => v.ValidityVariableId != "").Should().HaveCount(10).And.OnlyContain(v => v.ValidityVariableId == v.VariableId + "_validitySignal");
            ReportJson.Parse(prepared.ReportJson).Accepted.Should().BeTrue();
            (await ListAsync(http)).Should().NotContain(e => e.GetProperty("id").GetProperty("value").GetString() == id.Value, "prepare writes nothing");

            // 2. Commit with the endpoints of every variable: live, with the final report.
            var commit = new CommitRequest { Manifest = prepared.Manifest };
            commit.Endpoints.AddRange(prepared.Variables.Select(v => new GrpcVariableEndpoint { VariableId = v.VariableId, EndpointUrl = "opc.tcp://rig:4840", NamespaceUri = "http://ddhub.no/SmartConnect-02/Variables/", NodeId = "SmartConnect-02." + v.VariableId }));
            var committed = await client.CommitManifestAsync(commit);
            committed.Accepted.Should().BeTrue();
            var report = ReportJson.Parse(committed.ReportJson);
            report.ManifestId.Should().Be(id.Value);
            report.Statistics.CommitMs.Should().BeGreaterThan(0);
            report.Statistics.Inferred.Should().BeGreaterThan(0);

            // 3. A second commit finds nothing staged; preparing the same content again says it is live.
            var again = async () => await client.CommitManifestAsync(commit);
            (await again.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
            var live = await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = json });
            live.AlreadyLive.Should().BeTrue();
            live.ContentHash.Should().Be(prepared.ContentHash);
            ReportJson.Parse(live.ReportJson).Should().BeEquivalentTo(report);

            // 4. The list for the rebuild: the manifest, its variables and its endpoints.
            var list = await client.ListManifestsAsync(new Empty());
            var entry = list.Manifests.Should().ContainSingle(m => m.Manifest.Provider == "SmartConnect-02").Which;
            entry.Accepted.Should().BeTrue();
            entry.VocabularyVersion.Should().Be(_host.Version);
            entry.InjectedAt.ToDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
            entry.Variables.Should().HaveCount(20);
            entry.Endpoints.Should().HaveCount(20).And.OnlyContain(e => e.EndpointUrl == "opc.tcp://rig:4840" && e.NodeId == "SmartConnect-02." + e.VariableId);

            // 5. Query: a SELECT with rows; anything else is refused.
            var rows = await client.QueryAsync(new SparqlRequest { Query = $"PREFIX dwis: <http://d-wis.org/> SELECT ?dp ?signal WHERE {{ GRAPH <{id.AssertedGraph}> {{ ?dp dwis:HasDynamicValue ?signal }} }}" });
            rows.Variables.Should().Equal("dp", "signal");
            rows.Rows.Should().HaveCount(10).And.OnlyContain(r => r.Values.Count == 2 && r.Values[0].StartsWith("http://d-wis.org/SmartConnect-02/"));
            rows.Truncated.Should().BeFalse();
            var ask = async () => await client.QueryAsync(new SparqlRequest { Query = "ASK { ?s ?p ?o }" });
            (await ask.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

            // 6. Resources: the individuals and the sentences of the live description, asserted only.
            var resources = await client.GetResourcesAsync(new Empty());
            resources.Individuals.Should().Contain(i => i.Iri.StartsWith("http://d-wis.org/SmartConnect-02/") && i.TypeIri.StartsWith("http://d-wis.org/"));
            resources.Sentences.Should().Contain(s => s.Verb == "http://d-wis.org/HasDynamicValue" && s.Subject.StartsWith("http://d-wis.org/SmartConnect-02/"));
            resources.Individuals.Should().NotContain(i => i.TypeIri == "http://d-wis.org/PrototypeData", "inferred types are not resources");

            // 7. Remove: the graphs and the endpoints go; a second remove says nothing was there.
            (await client.RemoveManifestAsync(prepared.Manifest)).Removed.Should().BeTrue();
            (await client.ListManifestsAsync(new Empty())).Manifests.Should().NotContain(m => m.Manifest.Provider == "SmartConnect-02");
            (await AskAsync(http, $"ASK {{ GRAPH <{Ingest.Endpoints.Graph}> {{ ?v <{Provenance.ManifestIdProperty}> \"{id.Value}\" }} }}")).Should().BeFalse("the endpoints went with the manifest");
            (await client.RemoveManifestAsync(prepared.Manifest)).Removed.Should().BeFalse();

            // 8. Abort: a staged manifest dropped before its commit.
            var part03 = await File.ReadAllTextAsync(HostFixture.ManifestPath(Part03));
            var staged = await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = part03 });
            await client.AbortManifestAsync(staged.Manifest);
            var abortAgain = async () => await client.AbortManifestAsync(staged.Manifest);
            (await abortAgain.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
            var commitAborted = async () => await client.CommitManifestAsync(new CommitRequest { Manifest = staged.Manifest });
            (await commitAborted.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);

            // 9. Bad input.
            var notJson = async () => await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = "not json" });
            (await notJson.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
            var noRef = async () => await client.RemoveManifestAsync(new ManifestRef());
            (await noRef.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }
        finally
        {
            using var cleanup = await http.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        }
    }

    [Fact]
    public async Task A_staged_manifest_expires_after_the_time_limit()
    {
        _host.SkipUnlessAvailable();
        using var factory = _host.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<StagingOptions>(o => o.TimeLimitSeconds = 1)));
        using var http = factory.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(http);
        using var channel = Channel(factory);
        var client = new GrpcSemantic.SemanticClient(channel);
        var json = await File.ReadAllTextAsync(HostFixture.ManifestPath(Part03));
        var id = ManifestId.Of(ManifestJson.Parse(json));
        try
        {
            var staged = await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = json });
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            var late = async () => await client.CommitManifestAsync(new CommitRequest { Manifest = staged.Manifest });
            var refused = (await late.Should().ThrowAsync<RpcException>()).Which;
            refused.StatusCode.Should().Be(StatusCode.NotFound);
            refused.Status.Detail.Should().Contain("prepare it again");

            // Prepared again and committed in time, without endpoints: live.
            staged = await client.PrepareManifestAsync(new PrepareRequest { ManifestJson = json });
            (await client.CommitManifestAsync(new CommitRequest { Manifest = staged.Manifest })).Accepted.Should().BeTrue();
            (await client.ListManifestsAsync(new Empty())).Manifests.Should().ContainSingle(m => m.Manifest.Provider == "SmartConnect-03").Which.Endpoints.Should().BeEmpty();
        }
        finally
        {
            using var cleanup = await http.DeleteAsync(new Uri($"/api/v1/manifests/{id.Value}", UriKind.Relative));
        }
    }

    private static GrpcChannel Channel(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
}
