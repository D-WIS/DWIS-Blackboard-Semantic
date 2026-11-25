using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DWIS.Semantic.Ingest;
using static DWIS.Semantic.Host.Tests.HostClient;

namespace DWIS.Semantic.Host.Tests;

/// <summary>
/// The manifest library (M4.F, Tests #21; M5.F, Semantic #41): the five real manifests of
/// <c>Fixtures/manifests/library</c>, each injected next to the SmartConnect manifest, each report compared
/// with the expected one the way <c>tools/library/check-library.py</c> of the Tests repository compares them in
/// the smoke: the status, the manifest id, <c>accepted</c>, the vocabulary version, the counts, the live parts and
/// the findings sorted, without the timings and sizes. The expected reports are the same files on both sides.
/// </summary>
[Collection(SharedHost.Name)]
public sealed class ManifestLibraryTests
{
    /// <summary>The vocabulary the expected reports were recorded on; the host loads the same files under a version of its own.</summary>
    private const string PinnedVocabulary = "2026-10-14";

    private static readonly string[] StableFindingFields = ["stage", "severity", "code", "message", "focus", "path", "value", "source"];
    private static readonly ManifestId SmartConnect = ManifestId.Of("SmartConnect", "SmartConnect rig signals");
    private readonly HostFixture _host;

    public ManifestLibraryTests(HostFixture host)
    {
        _host = host;
    }

    /// <summary>Where the library is.</summary>
    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifests", "library");

    [Fact]
    public async Task Every_manifest_of_the_library_gets_its_expected_report_next_to_the_SmartConnect_manifest()
    {
        _host.SkipUnlessAvailable();
        using var client = _host.CreateClient();
        await HostFixture.WaitUntilLoadedAsync(client);
        var folders = Directory.GetDirectories(LibraryPath)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        folders.Select(Path.GetFileName).Should().Equal("norce-digital-twin", "openlab-2023-wp2", "openlab-rig-signals", "well-a-rig-data", "well-a-transient-model");

        var live = new List<string>();
        var differences = new List<string>();
        try
        {
            // The SmartConnect manifest first: the library goes live next to it, and it must still answer once at the end.
            (await PostAsync(client, "smartconnect.manifest.json")).Report.Accepted.Should().BeTrue();
            live.Add(SmartConnect.Value);
            var byClass = await SelectAsync(client, SppByClass);
            byClass.Should().ContainSingle();

            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                using var content = new StringContent(await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json")), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(new Uri("/api/v1/manifests", UriKind.Relative), content);
                var actual = Trim((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                if (actual["manifestId"] is JsonValue id)
                {
                    live.Add(id.GetValue<string>());
                }

                var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "expected-report.json")))!.AsObject();
                if (expected["vocabularyVersion"] is JsonValue version)
                {
                    version.GetValue<string>().Should().Be(PinnedVocabulary, $"{name}: the expected reports are recorded on the pinned vocabulary");
                    expected["vocabularyVersion"] = _host.Version;
                }

                if (!JsonNode.DeepEquals(Canonical(expected), Canonical(actual)))
                {
                    differences.Add($"{name}:\n  expected: {Canonical(expected).ToJsonString()}\n  actual:   {Canonical(actual).ToJsonString()}");
                }
            }

            differences.Should().BeEmpty("every report of the library is as expected; a difference that is right is recorded again in both repositories:\n" + string.Join("\n", differences));

            // Four of the five are live next to the SmartConnect manifest (the 2023 one is refused), each listed once.
            live.Should().HaveCount(5);
            var list = await ListAsync(client);
            foreach (var id in live)
            {
                list.Should().ContainSingle(e => e.GetProperty("id").GetProperty("value").GetString() == id, id);
            }

            // The F.3 query by class now answers three signals: the SmartConnect one and the standpipe pressure of two library providers.
            (await SelectAsync(client, SppByClass)).Should().BeEquivalentTo([.. byClass, "http://d-wis.org/openLAB/Variables/openLAB.SPP", "http://d-wis.org/WellARig/Variables/WellARig.spp"]);
            (await AskAsync(client, LegacyIriLeft)).Should().BeFalse();
        }
        finally
        {
            foreach (var id in live)
            {
                using var cleanup = await client.DeleteAsync(new Uri($"/api/v1/manifests/{id}", UriKind.Relative));
            }
        }
    }

    /// <summary>The stable part of an answer, as <c>check-library.py</c> trims it: a report, or an error body when the service refused the manifest.</summary>
    private static JsonObject Trim(int status, string body)
    {
        JsonNode? parsed;
        try
        {
            parsed = body.Length == 0 ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            parsed = JsonValue.Create(body);
        }

        if (parsed is not JsonObject report || !report.ContainsKey("findings"))
        {
            if (parsed is JsonObject problem)
            {
                problem.Remove("traceId"); // the trace id of a problem answer changes per run
            }

            return new JsonObject { ["status"] = status, ["answer"] = parsed };
        }

        var findings = new JsonArray();
        foreach (var finding in report["findings"]!.AsArray())
        {
            var trimmed = new JsonObject();
            foreach (var field in StableFindingFields)
            {
                if (finding![field] is JsonNode value && value.GetValueKind() != JsonValueKind.Null)
                {
                    trimmed[field] = value.DeepClone();
                }
            }

            findings.Add(trimmed);
        }

        return new JsonObject
        {
            ["status"] = status,
            ["manifestId"] = report["manifestId"]?.DeepClone(),
            ["accepted"] = report["accepted"]?.DeepClone(),
            ["vocabularyVersion"] = report["vocabularyVersion"]?.DeepClone(),
            ["counts"] = report["counts"]?.DeepClone(),
            ["live"] = report["live"]?.DeepClone(),
            ["findings"] = findings,
        };
    }

    /// <summary>The same report with its findings sorted by their serialisation with sorted keys, parsed again so that both sides compare alike.</summary>
    private static JsonObject Canonical(JsonObject report)
    {
        var copy = JsonNode.Parse(report.ToJsonString())!.AsObject();
        if (copy["findings"] is JsonArray findings)
        {
            var sorted = findings.Select(f => f!.DeepClone()).OrderBy(SortKey, StringComparer.Ordinal).ToList();
            copy["findings"] = new JsonArray([.. sorted]);
        }

        return copy;
    }

    private static string SortKey(JsonNode finding)
    {
        var withSortedKeys = new JsonObject(finding.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        return withSortedKeys.ToJsonString();
    }
}
