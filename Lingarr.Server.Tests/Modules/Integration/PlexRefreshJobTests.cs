using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
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
/// PlexRefreshJob contracts (B03): the settings re-check on execution (the
/// enqueue side already gated on the switch — a burst enqueued right before
/// the user disables the feature must unwind as a fast success), the
/// unconfigured fast success, and the in-process guard that merges an
/// overlapping trigger without waiting out the DisableConcurrentExecution
/// lock (the EmbeddedTrackScanJob _scanInProgress precedent). The job runs
/// against the real PlexService graph over the stub HTTP handler; the
/// cancellation contract is pinned too — real cancellation rethrows, a
/// timeout is the service's warning, never the job's exception.
/// </summary>
public class PlexRefreshJobTests
{
    private const string Token = "plex-token-job";

    private readonly Mock<ISettingService> _settingService = new();
    private readonly StubHttpMessageHandler _handler = new();
    private readonly PlexRefreshJob _job;

    public PlexRefreshJobTests()
    {
        var plexService = new PlexService(
            _handler.ToClient(),
            new PlexSettingsProvider(_settingService.Object, NullLogger<PlexSettingsProvider>.Instance),
            NullLogger<PlexService>.Instance);
        _job = new PlexRefreshJob(
            _settingService.Object,
            plexService,
            NullLogger<PlexRefreshJob>.Instance);
    }

    private void Configure(string? url, string? token = Token, string? enabled = "true")
    {
        _settingService
            .Setup(service => service.GetSetting(SettingKeys.Integration.PlexRefreshEnabled))
            .ReturnsAsync(enabled);
        _settingService
            .Setup(service => service.GetSetting(SettingKeys.Integration.PlexUrl))
            .ReturnsAsync(url);
        _settingService
            .Setup(service => service.GetEncryptedSetting(SettingKeys.Integration.PlexToken))
            .ReturnsAsync(token);
    }

    private static string SectionsJson => """
        {"MediaContainer":{"size":1,"Directory":[
            {"key":"1","title":"Movies","type":"movie"}
        ]}}
        """;

    [Fact]
    public async Task Execute_SwitchOffOrMissing_SucceedsFastWithoutTouchingPlex()
    {
        // B03 acceptance 3 on the job side: disabled (or never seeded) means
        // one log line — no settings probe, no socket.
        Configure("http://plex-job-off.test:32400", enabled: "false");

        await _job.Execute(CancellationToken.None);

        Assert.Empty(_handler.Requests);
        _settingService.Verify(
            service => service.GetEncryptedSetting(SettingKeys.Integration.PlexToken),
            Times.Never);
    }

    [Fact]
    public async Task Execute_EnabledButUnconfigured_SucceedsFastWithoutHttpCalls()
    {
        // Enabled with a missing URL (fresh install, switch flipped first)
        // is an expected state — fast success, no retry storm.
        Configure(url: null);

        await _job.Execute(CancellationToken.None);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Execute_EnabledAndConfigured_RefreshesEveryLibrary()
    {
        Configure("http://plex-job-on.test:32400/");
        _handler.EnqueueJson(SectionsJson);
        _handler.EnqueueStatus(HttpStatusCode.OK);

        await _job.Execute(CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        var refresh = _handler.Requests.Single(request => request.Method == HttpMethod.Post);
        Assert.Equal("http://plex-job-on.test:32400/library/sections/all/refresh",
            refresh.RequestUri!.ToString());
    }

    [Fact]
    public async Task Execute_WhileAnotherRefreshRuns_MergesWithoutCallingPlex()
    {
        // The guard: while one refresh hangs in its probe, an overlapping
        // trigger returns immediately — no lock wait, no second request.
        Configure("http://plex-job-guard.test:32400");
        _handler.EnqueueHang();

        using var cancellation = new CancellationTokenSource();
        var first = Task.Run(() => _job.Execute(cancellation.Token));
        await WaitUntilAsync(() => _handler.Requests.Count == 1,
            "the first refresh never reached its probe");

        await _job.Execute(CancellationToken.None); // overlapping trigger: guard path

        Assert.Single(_handler.Requests); // merged — Plex saw one probe only
        Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Get);

        // Real cancellation unwinds the hanging run (rethrow contract).
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(message);
    }
}
