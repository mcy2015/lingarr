using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Integration;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Integration;

public class RadarrServiceTests
{
    [Fact]
    public async Task GetMovies_DelegatesToStreamingEndpoint_ForRadarr()
    {
        // Arrange
        var integrationMock = new Mock<IIntegrationService>();
        integrationMock
            .Setup(s => s.StreamApiResponse<RadarrMovie>(
                It.IsAny<string>(),
                It.IsAny<IntegrationSettingKeys>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateMovieStream(1, 2, 3));

        var service = new RadarrService(integrationMock.Object);

        // Act
        var ids = new List<int>();
        await foreach (var movie in service.GetMovies())
        {
            ids.Add(movie.Id);
        }

        // Assert
        Assert.Equal(new List<int> { 1, 2, 3 }, ids);
        integrationMock.Verify(
            s => s.StreamApiResponse<RadarrMovie>(
                "/api/v3/movie/",
                It.Is<IntegrationSettingKeys>(keys =>
                    keys.Url == "radarr_url" && keys.ApiKey == "radarr_api_key"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static async IAsyncEnumerable<RadarrMovie> CreateMovieStream(params int[] ids)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            yield return new RadarrMovie
            {
                Id = id,
                Title = $"Movie {id}",
                Path = $"/movies/Movie {id}",
                RootFolderPath = "/movies",
                Added = "2026-01-01T00:00:00Z",
                HasFile = true
            };
        }
    }
}
