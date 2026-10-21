using Lingarr.Server.Modules.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Modules.Integration;

public class ImageService : IImageService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageService> _logger;

    public ImageService(IHttpClientFactory httpClientFactory, ILogger<ImageService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
    
    /// <inheritdoc />
    public async Task<IActionResult> GetApiResponse(string url)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                return new NotFoundResult();
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var stream = await response.Content.ReadAsStreamAsync();

            return new FileStreamResult(stream, contentType) { EnableRangeProcessing = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image proxy request failed for {Url}", LogSanitizer.Mask(url));
            return new NotFoundResult();
        }
    }
}