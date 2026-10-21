using System;
using System.Net;
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
/// *arr requests carry the apikey as a plaintext query parameter. With the
/// Serilog file sink, a connection failure whose exception message folds in
/// the request URI would persist that key to disk. These tests pin the guard
/// at the send boundary: sensitive exceptions escape masked (type and status
/// code preserved), clean ones escape untouched.
/// </summary>
public class IntegrationServiceSanitizationTests
{
    private const string ApiKey = "supersecret";

    private static readonly IntegrationSettingKeys RadarrKeys = new()
    {
        Url = "radarr_url",
        ApiKey = "radarr_api_key"
    };

    private static IntegrationService CreateService(HttpMessageHandler handler)
    {
        var settingsProvider = new Mock<IIntegrationSettingsProvider>();
        settingsProvider
            .Setup(p => p.GetSettings(It.IsAny<IntegrationSettingKeys>()))
            .ReturnsAsync(new IntegrationSettings { Url = "http://radarr.local", ApiKey = ApiKey });
        return new IntegrationService(new HttpClient(handler), settingsProvider.Object);
    }

    private static HttpRequestException UriCarryingException() => new(
        "GET http://radarr.local/api/v3/movie?apikey=" + ApiKey + " refused",
        new Exception("connect failed, target apikey=" + ApiKey),
        HttpStatusCode.BadGateway);

    [Fact]
    public async Task GetApiResponse_FailureWithUriInMessage_ThrowsMaskedWithoutApiKey()
    {
        var service = CreateService(new ThrowingHandler(UriCarryingException()));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.DoesNotContain(ApiKey, ex.ToString());
        Assert.Contains("apikey=***", ex.ToString());
    }

    [Fact]
    public async Task StreamApiResponse_FailureWithUriInMessage_ThrowsMaskedWithoutApiKey()
    {
        var service = CreateService(new ThrowingHandler(UriCarryingException()));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
            {
            }
        });

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.DoesNotContain(ApiKey, ex.ToString());
        Assert.Contains("apikey=***", ex.ToString());
    }

    [Fact]
    public async Task GetApiResponse_CleanConnectionFailure_IsRethrownUntouched()
    {
        var original = new HttpRequestException("No such host is known. (radarr.local:7878)");
        var service = CreateService(new ThrowingHandler(original));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys));

        Assert.Same(original, ex);
        Assert.Equal("No such host is known. (radarr.local:7878)", ex.Message);
    }

    [Fact]
    public async Task StreamApiResponse_CleanConnectionFailure_IsRethrownUntouched()
    {
        var original = new HttpRequestException("No such host is known. (radarr.local:7878)");
        var service = CreateService(new ThrowingHandler(original));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
            {
            }
        });

        Assert.Same(original, ex);
    }

    /// <summary>Simulates the transport-level failure HttpClient wraps around the request URI.</summary>
    private sealed class ThrowingHandler(HttpRequestException exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }
}
