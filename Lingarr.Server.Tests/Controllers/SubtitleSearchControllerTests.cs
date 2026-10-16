using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Manifests;
using Lingarr.Server.Tests.Modules.SubtitleSearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Controller contracts of the subtitle-search surface: the M1 skeleton
/// stays intact (unknown provider 404, non-probing sources 501) and the
/// M2 test endpoint runs the health probe for sources that implement
/// <see cref="ISubtitleProviderHealth"/> — provider failures land as
/// ok=false with a message, never a 500 (translate-test precedent). The
/// providers endpoint serves the real settings-card shape: manifest
/// fields passed through plus requiresAuth/credentialsConfigured, with
/// per-source degradation when the secret store cannot be read.
/// </summary>
public class SubtitleSearchControllerTests
{
    [SubtitleProvider("fakesubs", requiresAuth: true)]
    private class FakeProvider : ISubtitleProvider
    {
        public virtual string Provider => "fakesubs";

        public Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("Test double; M2 fills real downloads.");
    }

    [SubtitleProvider("probing")]
    private sealed class ProbingFakeProvider : FakeProvider, ISubtitleProviderHealth
    {
        public Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SubtitleProviderTestOutcome(true, "probe ok"));
    }

    [SubtitleProvider("failing")]
    private sealed class FailingProbeFakeProvider : FakeProvider, ISubtitleProviderHealth
    {
        public Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
            => throw SubtitleProviderException.ForMissingCredentials("failing", "API key");
    }

    // The seven v1 source keys as test doubles — attribute name and
    // Provider property match the production providers so the registry
    // keys and the endpoint answer line up; the real manifest classes
    // supply the settings forms.
    [SubtitleProvider("opensubtitles", requiresAuth: true)]
    private sealed class FakeOpenSubtitlesProvider : FakeProvider
    {
        public override string Provider => "opensubtitles";
    }

    [SubtitleProvider("subsource")]
    private sealed class FakeSubSourceProvider : FakeProvider
    {
        public override string Provider => "subsource";
    }

    [SubtitleProvider("subdl")]
    private sealed class FakeSubdlProvider : FakeProvider
    {
        public override string Provider => "subdl";
    }

    [SubtitleProvider("gestdown")]
    private sealed class FakeGestdownProvider : FakeProvider
    {
        public override string Provider => "gestdown";
    }

    [SubtitleProvider("podnapisi")]
    private sealed class FakePodnapisiProvider : FakeProvider
    {
        public override string Provider => "podnapisi";
    }

    [SubtitleProvider("assrt", requiresAuth: true)]
    private sealed class FakeAssrtProvider : FakeProvider
    {
        public override string Provider => "assrt";
    }

    [SubtitleProvider("shooter")]
    private sealed class FakeShooterProvider : FakeProvider
    {
        public override string Provider => "shooter";
    }

    private static ISubtitleProvider[] SevenSources() =>
    [
        new FakeOpenSubtitlesProvider(),
        new FakeSubSourceProvider(),
        new FakeSubdlProvider(),
        new FakeGestdownProvider(),
        new FakePodnapisiProvider(),
        new FakeAssrtProvider(),
        new FakeShooterProvider()
    ];

    private static ISubtitleSourceManifest[] RealManifests() =>
    [
        new OpenSubtitlesPluginManifest(),
        new SubSourcePluginManifest(),
        new SubdlPluginManifest(),
        new GestdownPluginManifest(),
        new PodnapisiPluginManifest(),
        new AssrtPluginManifest(),
        new ShooterPluginManifest()
    ];

    private static SubtitleSearchController CreateController(
        ISubtitleProvider[] providers,
        ISubtitleSourceManifest[]? manifests = null,
        Mock<ISettingsAccess>? settings = null)
        => new(
            new SubtitleProviderRegistry(
                providers,
                manifests ?? [],
                NullLogger<SubtitleProviderRegistry>.Instance),
            settings?.Object ?? SubtitleSettingsDouble.Create().Object,
            orchestrator: null!,
            queueService: null!,
            pinService: null!,
            pipeline: null!,
            requestBuilder: null!,
            NullLogger<SubtitleSearchController>.Instance);

    // Mirrors the production MVC options (InfrastructureModule): camelCase,
    // enums as strings, nulls dropped.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static JsonDocument Body(ObjectResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(result.Value, CamelCase));

    [Fact]
    public async Task TestProvider_UnknownProvider_Returns404WithProviderEcho()
    {
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<NotFoundObjectResult>(await controller.TestProvider("nosuch", CancellationToken.None));

        Assert.Equal(404, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("nosuch", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_NonProbingSource_KeepsTheStructured501()
    {
        // Sources without ISubtitleProviderHealth (test doubles, future
        // compiled-in sources before they gain a probe) keep the M1
        // contract: structured 501, not an exception.
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<ObjectResult>(
            await controller.TestProvider("fakesubs", CancellationToken.None));

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("fakesubs", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_ProbingSource_ReturnsOkResultWithLatency()
    {
        var controller = CreateController([new ProbingFakeProvider()]);

        var actionResult = await controller.TestProvider("probing", CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(actionResult);
        using var body = Body(result);

        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("probe ok", body.RootElement.GetProperty("message").GetString());
        // Measured around the probe — a real number, never decorative.
        Assert.True(body.RootElement.GetProperty("latencyMs").GetDouble() >= 0);
    }

    [Fact]
    public async Task TestProvider_ProbeFailure_ReturnsOkFalseWithReasonMessage()
    {
        // Provider failures are verdicts, not 500s (translate-test
        // precedent; PRD AC2 needs the readable bucket).
        var controller = CreateController([new FailingProbeFakeProvider()]);

        var actionResult = await controller.TestProvider("failing", CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(actionResult);
        using var body = Body(result);

        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("API key", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_LookupIsCaseInsensitive()
    {
        var controller = CreateController([new ProbingFakeProvider()]);

        var actionResult = await controller.TestProvider("Probing", CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
    }

    [Fact]
    public async Task GetProviders_Returns200WithAllSourcesInRegistryOrder()
    {
        var controller = CreateController(SevenSources(), RealManifests());

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        // One card per registered source, registry order, no extras.
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(7, body.RootElement.GetArrayLength());
        Assert.Equal(
            new[] { "opensubtitles", "subsource", "subdl", "gestdown", "podnapisi", "assrt", "shooter" },
            body.RootElement.EnumerateArray().Select(card => card.GetProperty("provider").GetString()).ToArray());

        // Manifest fields pass through untouched: display name,
        // description and the settings form with its field types.
        var opensubtitles = body.RootElement[0];
        Assert.Equal("OpenSubtitles", opensubtitles.GetProperty("displayName").GetString());
        Assert.Contains("OpenSubtitles.com", opensubtitles.GetProperty("description").GetString());
        var settings = opensubtitles.GetProperty("settings");
        Assert.Equal(2, settings.GetArrayLength());
        Assert.Equal(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, settings[0].GetProperty("key").GetString());
        // Enum member names in PascalCase — what the global
        // JsonStringEnumConverter emits and the settings UI matches on
        // (client types/plugin.ts maps 'Secret'/'Url').
        Assert.Equal("Secret", settings[0].GetProperty("type").GetString());
        Assert.Equal(SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, settings[1].GetProperty("key").GetString());
        Assert.Equal("Url", settings[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetProviders_RequiresAuthOnlyForTheTwoKeyedSources()
    {
        var controller = CreateController(SevenSources(), RealManifests());

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            var expected = card.GetProperty("provider").GetString() switch
            {
                "opensubtitles" => true,
                "assrt" => true,
                _ => false
            };
            Assert.Equal(expected, card.GetProperty("requiresAuth").GetBoolean());
        }
    }

    [Fact]
    public async Task GetProviders_StoredSecretMarksOnlyThatSourceConfigured()
    {
        var controller = CreateController(
            SevenSources(),
            RealManifests(),
            SubtitleSettingsDouble.Create(secrets: new Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.OpenSubtitles.ApiKey] = "os-api-key",
                // Whitespace-only secrets do not count as configured.
                [SubtitleSourceSettingKeys.Subdl.ApiKey] = "   "
            }));

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            var expected = card.GetProperty("provider").GetString() == "opensubtitles";
            Assert.Equal(expected, card.GetProperty("credentialsConfigured").GetBoolean());
        }
    }

    [Fact]
    public async Task GetProviders_ManifestLessEntryDegradesToProviderKeyAndEmptySettings()
    {
        // Test-double path (registry entries without a manifest) must not
        // throw: the card degrades to the provider key, null description
        // and an empty settings list.
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var card = body.RootElement[0];
        Assert.Equal("fakesubs", card.GetProperty("provider").GetString());
        Assert.Equal("fakesubs", card.GetProperty("displayName").GetString());
        // Null description — absent under WhenWritingNull, null otherwise.
        Assert.True(
            !card.TryGetProperty("description", out var description) || description.ValueKind == JsonValueKind.Null);
        Assert.Equal(0, card.GetProperty("settings").GetArrayLength());
        Assert.False(card.GetProperty("credentialsConfigured").GetBoolean());
    }

    [Fact]
    public async Task GetProviders_SecretReadFailureDegradesThatSourceAndKeepsTheList()
    {
        // One unreadable secret store must not 500 the settings page: the
        // failing source renders as unconfigured, the rest stay accurate.
        var settings = SubtitleSettingsDouble.Create(secrets: new Dictionary<string, string>
        {
            [SubtitleSourceSettingKeys.Assrt.Token] = "assrt-token"
        });
        settings
            .Setup(access => access.GetEncryptedSettingAsync(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey))
            .ThrowsAsync(new InvalidOperationException("settings store down"));

        var controller = CreateController(SevenSources(), RealManifests(), settings);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        Assert.Equal(7, body.RootElement.GetArrayLength());
        var byProvider = body.RootElement.EnumerateArray()
            .ToDictionary(card => card.GetProperty("provider").GetString());
        Assert.False(byProvider["opensubtitles"].GetProperty("credentialsConfigured").GetBoolean());
        Assert.True(byProvider["assrt"].GetProperty("credentialsConfigured").GetBoolean());
    }

    [Fact]
    public async Task GetProviders_NoSourcesRegistered_ReturnsEmptyList()
    {
        // Honest empty state: no source registered yet.
        var controller = CreateController([]);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());

        using var body = Body(result);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(0, body.RootElement.GetArrayLength());
    }
}
