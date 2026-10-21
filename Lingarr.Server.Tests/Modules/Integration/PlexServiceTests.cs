using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Tests.Modules.SubtitleSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Integration;

/// <summary>
/// Plex library refresh contracts (B03): the probe-then-poke request pair
/// (sections list with Accept JSON + X-Plex-Token header, then the
/// all-libraries refresh POST), the 10-minute section cache, trailing-slash
/// normalisation of user-entered URLs, and the best-effort ruling — a 5xx, a
/// network failure or a timeout is a warning, never an exception, and a
/// failed probe never issues the refresh. The HTTP layer is the
/// StubHttpMessageHandler shared with the subtitle-source provider tests
/// (requests recorded for URL/header assertions); the static section cache
/// is isolated per test through InvalidateSectionCache plus unique base
/// URLs, so the parallel job tests never share an entry.
/// </summary>
public class PlexServiceTests
{
    private const string Token = "plex-token-1";

    private readonly Mock<ISettingService> _settingService = new();
    private readonly StubHttpMessageHandler _handler = new();

    public PlexServiceTests()
    {
        PlexService.InvalidateSectionCache();
    }

    private PlexService CreateService(
        string? url = "http://plex-a.test:32400/",
        string? token = Token,
        TimeSpan? timeout = null)
    {
        _settingService
            .Setup(service => service.GetSetting(SettingKeys.Integration.PlexUrl))
            .ReturnsAsync(url);
        _settingService
            .Setup(service => service.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync(token);

        return new PlexService(
            timeout is { } span ? _handler.ToClient(span) : _handler.ToClient(),
            new PlexSettingsProvider(_settingService.Object, NullLogger<PlexSettingsProvider>.Instance),
            NullLogger<PlexService>.Instance);
    }

    private static string SectionsJson => """
        {"MediaContainer":{"size":2,"Directory":[
            {"allowSync":false,"key":"1","title":"Movies","type":"movie"},
            {"allowSync":false,"key":"2","title":"TV Shows","type":"show"}
        ]}}
        """;

    [Fact]
    public async Task RefreshLibraryAsync_ProbesSectionsThenRefreshesAllLibraries()
    {
        _handler.EnqueueJson(SectionsJson);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService(url: "http://plex-a.test:32400/"); // trailing slash must be normalised

        var refreshed = await service.RefreshLibraryAsync();

        Assert.True(refreshed);
        Assert.Equal(2, _handler.Requests.Count);

        var probe = _handler.Requests[0];
        Assert.Equal(HttpMethod.Get, probe.Method);
        Assert.Equal("http://plex-a.test:32400/library/sections", probe.RequestUri!.ToString());
        Assert.Contains("application/json", probe.Headers.Accept.ToString());
        Assert.Equal(Token, probe.Headers.GetValues("X-Plex-Token").Single());

        var refresh = _handler.Requests[1];
        Assert.Equal(HttpMethod.Post, refresh.Method);
        Assert.Equal("http://plex-a.test:32400/library/sections/all/refresh", refresh.RequestUri!.ToString());
        Assert.Equal(Token, refresh.Headers.GetValues("X-Plex-Token").Single());
    }

    [Fact]
    public async Task RefreshLibraryAsync_SecondCallWithinTtl_ReusesTheCachedSectionList()
    {
        _handler.EnqueueJson(SectionsJson);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService(url: "http://plex-b.test:32400");

        Assert.True(await service.RefreshLibraryAsync());
        Assert.True(await service.RefreshLibraryAsync());

        // One probe for two refreshes — the landing storm may not hammer
        // the sections endpoint.
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Method);
        Assert.All(_handler.Requests.Skip(1), request =>
            Assert.Equal(HttpMethod.Post, request.Method));
    }

    [Fact]
    public async Task RefreshLibraryAsync_ExpiredCache_ReprobesTheSectionList()
    {
        var originalTtl = PlexService.SectionCacheTtl;
        PlexService.SectionCacheTtl = TimeSpan.Zero;
        try
        {
            _handler.EnqueueJson(SectionsJson);
            _handler.EnqueueStatus(HttpStatusCode.OK);
            _handler.EnqueueJson(SectionsJson);
            _handler.EnqueueStatus(HttpStatusCode.OK);
            var service = CreateService(url: "http://plex-c.test:32400");

            Assert.True(await service.RefreshLibraryAsync());
            Assert.True(await service.RefreshLibraryAsync());

            Assert.Equal(4, _handler.Requests.Count);
            Assert.Equal(2, _handler.Requests.Count(request => request.Method == HttpMethod.Get));
        }
        finally
        {
            PlexService.SectionCacheTtl = originalTtl;
        }
    }

    [Fact]
    public async Task RefreshLibraryAsync_SectionsProbeReturns5xx_AbortsWithoutRefresh()
    {
        // A failed probe targets the same server the refresh would — no
        // point poking it twice.
        _handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        var service = CreateService(url: "http://plex-d.test:32400");

        var refreshed = await service.RefreshLibraryAsync();

        Assert.False(refreshed);
        Assert.Single(_handler.Requests); // no POST left the door
    }

    [Fact]
    public async Task RefreshLibraryAsync_UnparseableSectionList_AbortsWithoutRefresh()
    {
        _handler.EnqueueJson("<html>login page</html>");
        var service = CreateService(url: "http://plex-e.test:32400");

        var refreshed = await service.RefreshLibraryAsync();

        Assert.False(refreshed);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task RefreshLibraryAsync_RefreshPostReturns5xx_ReturnsFalseWithoutThrowing()
    {
        _handler.EnqueueJson(SectionsJson);
        _handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(url: "http://plex-f.test:32400");

        var refreshed = await service.RefreshLibraryAsync();

        Assert.False(refreshed); // warned, not thrown
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task RefreshLibraryAsync_TimeoutOnTheProbe_WarnsInsteadOfThrowing()
    {
        // Hanging responder + a 50ms client timeout exercises the timeout
        // bucket without wall-clock waits (the stub handler precedent).
        _handler.EnqueueHang();
        var service = CreateService(url: "http://plex-g.test:32400", timeout: TimeSpan.FromMilliseconds(50));

        var refreshed = await service.RefreshLibraryAsync();

        Assert.False(refreshed);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task RefreshLibraryAsync_UnconfiguredUrlOrToken_MakesNoHttpRequests()
    {
        // Missing URL alone, then missing token alone — either gap means
        // "not configured" (provider returns null) and no socket opens.
        var noUrl = CreateService(url: null);
        Assert.False(await noUrl.RefreshLibraryAsync());

        var noToken = CreateService(url: "http://plex-h.test:32400", token: null);
        Assert.False(await noToken.RefreshLibraryAsync());

        Assert.Empty(_handler.Requests);
        // The token rides the encrypted channel (never GetSetting), and the
        // provider reads both values before judging the gap — the
        // IntegrationSettingsProvider shape.
        _settingService.Verify(
            service => service.GetEncryptedSetting(SettingKeys.Integration.PlexToken),
            Times.Exactly(2));
    }
}
