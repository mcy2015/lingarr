using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Telemetry;

/// <summary>
/// Search/download telemetry of the subtitle-search feature (design ④/⑩):
/// one <c>subtitle_search_sessions</c> row per source query (zero-result
/// queries included — that is what keeps the hit-rate KPI honest) and the
/// <c>provider_quota_usage</c> daily counters, upserted after every
/// Search/Download with a single <c>INSERT … ON CONFLICT DO UPDATE</c> per
/// call (⑩). Rows are written through the scoped
/// <see cref="LingarrDbContext"/>; callers decide the SaveChanges rhythm
/// (the job saves once per media item, the manual endpoints per request).
/// </summary>
public sealed class SubtitleTelemetryService
{
    private readonly LingarrDbContext _dbContext;

    public SubtitleTelemetryService(LingarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Appends one search-session row and returns its id (saved immediately
    /// — the download pipeline links its history row to the deciding
    /// session). <paramref name="queryLanguage"/> is the comma-joined
    /// language set of the query: the session records what was asked, one
    /// row per source per round.
    /// </summary>
    public async Task<int> RecordSearchSessionAsync(
        MediaType mediaType,
        int mediaId,
        string provider,
        string queryLanguage,
        int resultCount,
        bool hitTarget)
    {
        var session = new SubtitleSearchSession
        {
            MediaType = mediaType,
            MediaId = mediaId,
            Provider = provider,
            QueryLanguage = queryLanguage,
            ResultCount = resultCount,
            HitTarget = hitTarget,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.SubtitleSearchSessions.Add(session);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch
        {
            // A failed save leaves the session tracked in Added state and
            // every later SaveChanges on this scoped context would retry
            // (and fail with) the same row — poisoning the whole job round
            // (QA2 P2-2). Detach so one telemetry hiccup stays contained;
            // the caller keeps the "telemetry is monitoring, not business
            // logic" ruling and logs. The quota upsert needs no such guard:
            // raw SQL tracks nothing.
            _dbContext.Entry(session).State = EntityState.Detached;
            throw;
        }

        return session.Id;
    }

    /// <summary>
    /// Increments the provider's daily search counter (quota upsert, ⑩).
    /// <paramref name="quotaLimit"/> carries the source's published daily
    /// limit for the waterline column (0 = none published) — display only,
    /// never a circuit breaker.
    /// </summary>
    public Task RecordSearchQuotaAsync(string provider, int quotaLimit = 0)
        => UpsertQuotaAsync(provider, searches: 1, downloads: 0, quotaLimit, quotaRemaining: null);

    /// <summary>
    /// Increments the provider's daily download counter (quota upsert, ⑩).
    /// <paramref name="quotaRemaining"/> stores the remaining-quota value a
    /// source reports (assrt); null keeps the previous value.
    /// </summary>
    public Task RecordDownloadQuotaAsync(string provider, int quotaLimit = 0, int? quotaRemaining = null)
        => UpsertQuotaAsync(provider, searches: 0, downloads: 1, quotaLimit, quotaRemaining);

    /// <summary>
    /// The published daily download limit of a provider for the quota
    /// waterline — the single place a source constant lives (⑩). Only
    /// OpenSubtitles publishes one (40/day); 0 means "none published".
    /// </summary>
    public static int PublishedQuotaLimit(string provider)
        => provider.Equals("opensubtitles", StringComparison.OrdinalIgnoreCase)
            ? OpenSubtitlesProvider.DailyDownloadQuota
            : 0;

    /// <summary>
    /// <c>INSERT … ON CONFLICT(date, provider) DO UPDATE</c> — SQLite native
    /// upsert so concurrent rounds cannot lose counts. EF has no increment
    /// upsert, hence the raw SQL with explicit SQLite parameters.
    /// </summary>
    private async Task UpsertQuotaAsync(
        string provider,
        int searches,
        int downloads,
        int quotaLimit,
        int? quotaRemaining)
    {
        var date = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var parameters = new object[]
        {
            new Microsoft.Data.Sqlite.SqliteParameter("@date", date),
            new Microsoft.Data.Sqlite.SqliteParameter("@provider", provider),
            new Microsoft.Data.Sqlite.SqliteParameter("@searches", searches),
            new Microsoft.Data.Sqlite.SqliteParameter("@downloads", downloads),
            new Microsoft.Data.Sqlite.SqliteParameter("@quotaLimit", quotaLimit),
            // Microsoft.Data.Sqlite refuses to bind a raw null — an unboxed
            // null here throws "Value must be set", which (before this fix)
            // silently killed every quota upsert AND, because the orchestrator
            // calls quota before the session save inside one try, every
            // session row too. DBNull.Value is the required spelling.
            new Microsoft.Data.Sqlite.SqliteParameter("@quotaRemaining", (object?)quotaRemaining ?? DBNull.Value)
        };
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO provider_quota_usage (date, provider, searches, downloads, quota_limit, quota_remaining)
            VALUES (@date, @provider, @searches, @downloads, @quotaLimit, @quotaRemaining)
            ON CONFLICT(date, provider) DO UPDATE SET
                searches = searches + @searches,
                downloads = downloads + @downloads,
                quota_limit = @quotaLimit,
                quota_remaining = COALESCE(@quotaRemaining, quota_remaining)
            """,
            parameters);
    }
}
