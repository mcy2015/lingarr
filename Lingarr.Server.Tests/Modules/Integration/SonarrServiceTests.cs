using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Integration;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Integration;

/// <summary>
/// QA4 P2-1: GetShows consumes the series payload through the streaming
/// endpoint (ResponseHeadersRead + element-by-element deserialization of the
/// top-level array — the Radarr precedent) instead of buffering the whole
/// JSON document via GetApiResponse next to its materialized list. These
/// tests pin the delegation and the preserved "not configured → null"
/// contract of the buffering wrapper; the streaming semantics themselves
/// (incremental yields, stream disposal) live in
/// IntegrationServiceStreamingTests.
/// </summary>
public class SonarrServiceTests
{
    private static async IAsyncEnumerable<SonarrShow> CreateShowStream(params int[] ids)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            yield return new SonarrShow
            {
                Id = id,
                Title = $"Show {id}",
                Path = $"/tv/Show {id}",
                Added = "2026-01-01T00:00:00Z",
                SeasonFolder = true
            };
        }
    }

    [Fact]
    public async Task GetShows_StreamsSeriesThroughTheStreamingEndpoint()
    {
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.StreamApiResponse<SonarrShow>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateShowStream(1, 2, 3));

        var service = new SonarrService(integrationMock.Object);

        var shows = await service.GetShows();

        // every element of the stream reaches the buffered result, in order
        Assert.NotNull(shows);
        Assert.Equal(new List<int> { 1, 2, 3 }, shows!.Select(show => show.Id).ToList());
        integrationMock.Verify(
            s => s.StreamApiResponse<SonarrShow>(
                "/api/v3/series/",
                It.Is<IntegrationSettingKeys>(keys =>
                    keys.Url == "sonarr_url" && keys.ApiKey == "sonarr_api_key"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetShows_EmptyLibrary_ReturnsEmptyList()
    {
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.StreamApiResponse<SonarrShow>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateShowStream());

        var shows = await new SonarrService(integrationMock.Object).GetShows();

        Assert.NotNull(shows);
        Assert.Empty(shows!);
    }

    [Fact]
    public async Task GetShows_NotConfiguredSettings_ReturnNullInsteadOfThrowing()
    {
        // StreamApiResponse signals "integration not configured" by throwing
        // (an async stream cannot yield null); GetShows translates that back
        // into the historical GetApiResponse contract so the sync job skips
        // the run as before.
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.StreamApiResponse<SonarrShow>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException(
                "Integration settings for 'sonarr_url' are not configured correctly."));

        var shows = await new SonarrService(integrationMock.Object).GetShows();

        Assert.Null(shows);
    }

    [Fact]
    public async Task GetShows_HttpFailure_PropagatesToCaller()
    {
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.StreamApiResponse<SonarrShow>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>(),
                It.IsAny<CancellationToken>()))
            .Throws(new HttpRequestException("Integration request failed: InternalServerError"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new SonarrService(integrationMock.Object).GetShows());
    }

    /// <summary>
    /// Sonarr's episode list endpoint defaults includeEpisodeFile to false and
    /// then serves episodes WITHOUT their episodeFile resource (hasFile is an
    /// independent scalar and stays true) — every path, dateAdded and
    /// mediaInfo.audioLanguages silently deserialized to null, which left all
    /// episodes without file paths and the embedded-track scan nothing to
    /// probe. The URL must opt in explicitly.
    /// </summary>
    [Fact]
    public async Task GetEpisodes_RequestsEpisodeFilesExplicitly()
    {
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.GetApiResponse<List<SonarrEpisode>>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>()))
            .ReturnsAsync(new List<SonarrEpisode>());

        await new SonarrService(integrationMock.Object).GetEpisodes(29, 1);

        integrationMock.Verify(
            s => s.GetApiResponse<List<SonarrEpisode>>(
                "/api/v3/episode?seriesId=29&seasonNumber=1&includeEpisodeFile=true",
                It.Is<IntegrationSettingKeys>(keys =>
                    keys.Url == "sonarr_url" && keys.ApiKey == "sonarr_api_key")),
            Times.Once);
    }
}
