using System.Net;
using System.Text.Json;
using DWIS.Semantic.Store;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DWIS.Semantic.Host.Tests;

/// <summary><c>GET /api/v1/health</c> and <c>GET /api/v1/vocabulary</c>.</summary>
[Collection(SharedHost.Name)]
public sealed class HealthTests
{
    private readonly HostFixture _host;

    public HealthTests(HostFixture host)
    {
        _host = host;
    }

    [Fact]
    public async Task Health_is_503_with_the_reasons_when_the_store_is_unreachable()
    {
        // No Fuseki needed: a store address nobody listens on.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton(new FusekiOptions { BaseUrl = new Uri("http://127.0.0.1:1/") })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/health", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        json.RootElement.GetProperty("checks").GetProperty("store").GetProperty("status").GetString().Should().Be("Unhealthy");
        json.RootElement.GetProperty("checks").GetProperty("vocabulary").GetProperty("status").GetString().Should().Be("Unhealthy");

        using var manifest = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        manifest.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "no manifest is taken before the vocabulary is there");
    }

    [Fact]
    public async Task Health_is_200_once_the_store_answers_and_the_vocabulary_is_loaded()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var response = await client.GetAsync(new Uri("/api/v1/health", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        json.RootElement.GetProperty("checks").GetProperty("store").GetProperty("status").GetString().Should().Be("Healthy");
        json.RootElement.GetProperty("checks").GetProperty("vocabulary").GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task The_vocabulary_answer_names_the_graphs_the_sizes_and_the_policy()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);

        using var response = await client.GetAsync(new Uri("/api/v1/vocabulary", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        root.GetProperty("version").GetString().Should().Be(_host.Version);
        root.GetProperty("graph").GetString().Should().Be(_host.VocabularyGraph);
        root.GetProperty("unitsGraph").GetString().Should().Be(_host.UnitsGraph);
        root.GetProperty("loaded").GetBoolean().Should().BeTrue();
        root.GetProperty("triples").GetInt32().Should().BeGreaterThan(9000);
        root.GetProperty("unitTriples").GetInt32().Should().BeGreaterThan(7000);
        root.GetProperty("policyFile").GetString().Should().Be(HostFixture.PolicyPath);
        root.GetProperty("policyRules").GetInt32().Should().Be(4);
    }
}
