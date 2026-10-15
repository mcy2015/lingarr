using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// M1 skeleton contracts of the subtitle-search controller: the provider
/// test endpoint answers 501 with a structured body for known sources and
/// 404 for unknown keys; the providers list endpoint answers 501 with the
/// real registry contents. Everything else lands with the M2–M6 batches.
/// </summary>
public class SubtitleSearchControllerTests
{
    [SubtitleProvider("fakesubs", requiresAuth: true)]
    private sealed class FakeProvider : ISubtitleProvider
    {
        public string Provider => "fakesubs";

        public Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("Test double; M2 fills real downloads.");
    }

    private sealed class FakeManifest(string provider) : IPluginManifest
    {
        public string Provider { get; } = provider;
        public string DisplayName { get; } = $"Fake {provider}";
        public string? Description { get; } = null;
        public IReadOnlyList<PluginSettingField> Settings { get; } = [];
    }

    private static SubtitleSearchController CreateController(params ISubtitleProvider[] providers) => new(
        new SubtitleProviderRegistry(
            providers,
            Array.Empty<IPluginManifest>(),
            NullLogger<SubtitleProviderRegistry>.Instance));

    private static JsonDocument Body(ObjectResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(result.Value));

    [Fact]
    public void TestProvider_UnknownProvider_Returns404WithProviderEcho()
    {
        var controller = CreateController(new FakeProvider());

        var result = Assert.IsType<NotFoundObjectResult>(controller.TestProvider("nosuch"));

        Assert.Equal(404, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("nosuch", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void TestProvider_RegisteredProvider_Returns501WithStructuredBody()
    {
        // DI mounted, registry resolving, endpoint reachable — the M2 batch
        // replaces the 501 with the real credentials/connectivity/quota
        // probe.
        var controller = CreateController(new FakeProvider());

        var result = Assert.IsType<ObjectResult>(controller.TestProvider("fakesubs"));

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("fakesubs", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void TestProvider_LookupIsCaseInsensitive()
    {
        var controller = CreateController(new FakeProvider());

        var result = Assert.IsType<ObjectResult>(controller.TestProvider("FakeSubs"));

        Assert.Equal(501, result.StatusCode);
    }

    [Fact]
    public void GetProviders_Returns501WithRealRegistryContents()
    {
        var controller = CreateController(new FakeProvider());

        var result = Assert.IsType<ObjectResult>(controller.GetProviders());

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        var providers = body.RootElement.GetProperty("providers");
        Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        // Real registry data, not a placeholder list.
        Assert.Equal("fakesubs", providers[0].GetString());
    }

    [Fact]
    public void GetProviders_NoSourcesRegistered_Returns501WithEmptyList()
    {
        // Honest empty state: no source registered yet (the M1 module
        // registers none; M2 adds the seven v1 sources).
        var controller = CreateController();

        var result = Assert.IsType<ObjectResult>(controller.GetProviders());

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("providers").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("providers").GetArrayLength());
    }
}
