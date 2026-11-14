using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace DWIS.Semantic.Store.Tests;

/// <summary>The one Fuseki shared by the contract tests of this assembly.</summary>
[CollectionDefinition(Name)]
public sealed class SharedFuseki : ICollectionFixture<FusekiFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "fuseki";
}

/// <summary>
/// A Fuseki to test against (Appendix G.1, contract layer), found in this order:
/// <list type="number">
/// <item><c>DWIS_FUSEKI_URL</c>: a running Fuseki with the Rig Edition datasets (the compose stack of DWIS-Blackboard-Tests, for example).</item>
/// <item><c>DWIS_FUSEKI_IMAGE</c>: the Rig Edition image (<c>digiwells/dwis.blackboard.fuseki</c>), started with Testcontainers; CI builds it from DWIS-Blackboard-Tests and sets this.</item>
/// <item>Otherwise a throwaway image built here from the Fuseki jar of Maven Central and a copy of the dataset configuration (<c>Fixtures/fuseki</c>), started with Testcontainers.</item>
/// </list>
/// Without Docker or network the tests are skipped with the reason, unless <c>DWIS_FUSEKI_REQUIRED=true</c>, in which case they fail.
/// </summary>
public sealed class FusekiFixture : IAsyncLifetime
{
    /// <summary>Environment variable: a running Fuseki.</summary>
    public const string UrlVariable = "DWIS_FUSEKI_URL";

    /// <summary>Environment variable: an image to start.</summary>
    public const string ImageVariable = "DWIS_FUSEKI_IMAGE";

    /// <summary>Environment variable: fail instead of skipping when no Fuseki can be had.</summary>
    public const string RequiredVariable = "DWIS_FUSEKI_REQUIRED";

    private const int Port = 3030;
    private IContainer? _container;

    /// <summary>Why the tests cannot run, or null when they can.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Where the tests found Fuseki.</summary>
    public string Source { get; private set; } = "";

    /// <summary>The Fuseki address.</summary>
    public Uri BaseUrl { get; private set; } = new("http://localhost:3030/");

    /// <summary>The HTTP client the store uses.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>The client under test.</summary>
    public FusekiStore Store { get; private set; } = null!;

    /// <summary>Skips the calling test when no Fuseki is available.</summary>
    public void SkipUnlessAvailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var required = string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            var url = Environment.GetEnvironmentVariable(UrlVariable);
            if (!string.IsNullOrWhiteSpace(url))
            {
                BaseUrl = new Uri(url.EndsWith('/') ? url : url + "/");
                Source = $"{UrlVariable}={url}";
            }
            else
            {
                await StartContainerAsync().ConfigureAwait(false);
            }

            Store = new FusekiStore(Http, new FusekiOptions { BaseUrl = BaseUrl });
            if (!await Store.PingAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Fuseki at {BaseUrl} does not answer /$/ping.");
            }
        }
        catch (Exception e) when (!required)
        {
            SkipReason = $"No Fuseki to test against ({e.GetType().Name}: {e.Message}). Set {UrlVariable} to a running Fuseki or {ImageVariable} to an image, or make Docker and the network available; set {RequiredVariable}=true to fail instead of skipping.";
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StartContainerAsync()
    {
        IImage image;
        var imageName = Environment.GetEnvironmentVariable(ImageVariable);
        if (!string.IsNullOrWhiteSpace(imageName))
        {
            image = new DockerImage(imageName);
            Source = $"{ImageVariable}={imageName}";
        }
        else
        {
            var throwaway = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fuseki"))
                .WithDockerfile("Dockerfile")
                .WithName("dwis-blackboard-fuseki-test:6.2.0")
                .WithCleanUp(false)
                .Build();
            await throwaway.CreateAsync().ConfigureAwait(false);
            image = throwaway;
            Source = "throwaway image built from Fixtures/fuseki";
        }

        _container = new ContainerBuilder(image)

            .WithPortBinding(Port, true)
            .WithEnvironment("JAVA_OPTIONS", "-Xmx1g")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/$/ping")))
            .Build();
        await _container.StartAsync().ConfigureAwait(false);
        BaseUrl = new UriBuilder("http", _container.Hostname, _container.GetMappedPublicPort(Port)).Uri;
    }
}
