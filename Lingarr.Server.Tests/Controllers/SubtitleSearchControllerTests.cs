using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Controller contracts of the subtitle-search surface: the M1 skeleton
/// stays intact (unknown provider 404, non-probing sources 501, providers
/// list with real registry contents) and the M2 test endpoint runs the
/// health probe for sources that implement
/// <see cref="ISubtitleProviderHealth"/> — provider failures land as
/// ok=false with a message, never a 500 (translate-test precedent).
/// </summary>
public class SubtitleSearchControllerTests
{
    [SubtitleProvider("fakesubs", requiresAuth: true)]
    private class FakeProvider : ISubtitleProvider
    {
        public string Provider => "fakesubs";

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

    private static SubtitleSearchController CreateController(params ISubtitleProvider[] providers) => new(
        new SubtitleProviderRegistry(
            providers,
            Array.Empty<ISubtitleSourceManifest>(),
            NullLogger<SubtitleProviderRegistry>.Instance),
        orchestrator: null!,
        queueService: null!,
        pinService: null!,
        pipeline: null!,
        requestBuilder: null!,
        NullLogger<SubtitleSearchController>.Instance);

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private static JsonDocument Body(ObjectResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(result.Value, CamelCase));

    [Fact]
    public async Task TestProvider_UnknownProvider_Returns404WithProviderEcho()
    {
        var controller = CreateController(new FakeProvider());

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
        var controller = CreateController(new FakeProvider());

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
        var controller = CreateController(new ProbingFakeProvider());

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
        var controller = CreateController(new FailingProbeFakeProvider());

        var actionResult = await controller.TestProvider("failing", CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(actionResult);
        using var body = Body(result);

        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("API key", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_LookupIsCaseInsensitive()
    {
        var controller = CreateController(new ProbingFakeProvider());

        var actionResult = await controller.TestProvider("Probing", CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
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
        // Honest empty state: no source registered yet.
        var controller = CreateController();

        var result = Assert.IsType<ObjectResult>(controller.GetProviders());

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("providers").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("providers").GetArrayLength());
    }
}
