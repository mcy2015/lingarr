using System.Text.Json;

namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// Outbound Plex library refresh (B03, the bazarr media-server-notification
/// replacement): one refresh asks Plex to rescan every library so subtitles
/// that just landed appear without a manual "Scan library files".
/// <para>
/// The flow is a probe then a poke: <c>GET /library/sections</c> (parsed
/// directory list cached for <see cref="SectionCacheTtl"/> — a landing storm
/// enqueues several jobs and must not hammer the sections endpoint) followed
/// by <c>POST /library/sections/all/refresh</c>, which refreshes every
/// library in one call (v1 deliberately does not target sections
/// individually — Plex's own incremental scan is fast, per-section targeting
/// buys nothing). A failed probe aborts the refresh: it is the same server
/// either way.
/// </para>
/// <para>
/// Every failure is a warning, never an exception — refreshing is
/// best-effort and must not sink the job that triggered it. The token rides
/// in the X-Plex-Token header on both calls, never in the URL (a connection
/// failure would otherwise fold it into the exception message).
/// </para>
/// </summary>
public class PlexService
{
    /// <summary>
    /// Lifetime of the cached section list. Public and mutable for tests
    /// (the MediaController subtitle-file-cache TTL precedent); the value is
    /// read inside the cache gate, so a swap takes effect on the next call.
    /// </summary>
    public static TimeSpan SectionCacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cached (base URL → section titles) probe result. Static because the
    /// service itself is scoped per job activation — the 10-minute
    /// deduplication only means anything across activations. Keyed by the
    /// base URL so a settings change never serves another server's list.
    /// </summary>
    private static (string BaseUrl, DateTimeOffset FetchedAt, List<string> Sections)? _sectionCache;

    private static readonly object SectionCacheGate = new();

    private readonly HttpClient _httpClient;
    private readonly PlexSettingsProvider _settingsProvider;
    private readonly ILogger<PlexService> _logger;

    public PlexService(
        HttpClient httpClient,
        PlexSettingsProvider settingsProvider,
        ILogger<PlexService> logger)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    /// <summary>
    /// Drops the cached section list (test seam; also correct after a
    /// settings swap that keeps the URL but changes the token's server
    /// view).
    /// </summary>
    public static void InvalidateSectionCache()
    {
        lock (SectionCacheGate)
        {
            _sectionCache = null;
        }
    }

    /// <summary>
    /// Refreshes every Plex library. Returns true when the refresh request
    /// was accepted; false when the integration is unconfigured or any step
    /// failed (already logged as warnings). Never throws except for real
    /// caller cancellation.
    /// </summary>
    public async Task<bool> RefreshLibraryAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsProvider.GetSettings();
        if (settings is null)
        {
            return false; // the provider already logged which piece is missing
        }

        // Users paste the URL with or without a trailing slash — normalise
        // once, here, so both request paths below stay simple.
        var baseUrl = settings.Url.TrimEnd('/');

        var sections = await GetSectionTitlesAsync(baseUrl, settings.Token, cancellationToken);
        if (sections is null)
        {
            return false; // probe failed (logged) — the refresh targets the same server
        }

        return await PostRefreshAsync(baseUrl, settings.Token, cancellationToken);
    }

    /// <summary>
    /// Probe step: the directory list (logged so the log answers "what did
    /// the refresh cover") served from cache inside the TTL.
    /// </summary>
    private async Task<List<string>?> GetSectionTitlesAsync(
        string baseUrl, string token, CancellationToken cancellationToken)
    {
        List<string>? cached;
        lock (SectionCacheGate)
        {
            if (_sectionCache is { } cache
                && cache.BaseUrl == baseUrl
                && DateTimeOffset.UtcNow - cache.FetchedAt < SectionCacheTtl)
            {
                cached = cache.Sections;
            }
            else
            {
                cached = null;
            }
        }

        if (cached is not null)
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/library/sections");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Plex-Token", token);

        HttpResponseMessage? response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // real cancellation propagates; a timeout is handled below
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Plex section list request to {BaseUrl} failed before a response (network error or timeout).",
                baseUrl);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Plex section list request to {BaseUrl} returned {StatusCode}.",
                    baseUrl, (int)response.StatusCode);
                return null;
            }

            List<string> titles;
            try
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                titles = ParseSectionTitles(payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Plex section list response from {BaseUrl} could not be parsed.",
                    baseUrl);
                return null;
            }

            lock (SectionCacheGate)
            {
                _sectionCache = (baseUrl, DateTimeOffset.UtcNow, titles);
            }

            _logger.LogInformation(
                "Plex libraries seen before refresh: {Sections}",
                string.Join(", ", titles));
            return titles;
        }
    }

    /// <summary>
    /// Extracts the directory titles from the JSON MediaContainer payload
    /// (Accept: application/json). Every entry's title is kept even when
    /// blank entries appear — the list is for logs, not for targeting.
    /// </summary>
    private static List<string> ParseSectionTitles(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var directories = document.RootElement
            .GetProperty("MediaContainer")
            .GetProperty("Directory");
        return directories.EnumerateArray()
            .Select(directory => directory.TryGetProperty("title", out var title)
                ? title.GetString() ?? string.Empty
                : string.Empty)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();
    }

    private async Task<bool> PostRefreshAsync(
        string baseUrl, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, baseUrl + "/library/sections/all/refresh");
        request.Headers.Add("X-Plex-Token", token);

        HttpResponseMessage? response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Plex library refresh request to {BaseUrl} failed before a response (network error or timeout).",
                baseUrl);
            return false;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Plex library refresh request to {BaseUrl} returned {StatusCode}.",
                    baseUrl, (int)response.StatusCode);
                return false;
            }

            _logger.LogInformation(
                "Plex library refresh accepted by {BaseUrl}; libraries rescan asynchronously.",
                baseUrl);
            return true;
        }
    }
}
