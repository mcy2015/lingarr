using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Requests;

/// <summary>
/// Builds <see cref="SubtitleSearchRequest"/>s from library entities
/// (tech design ②/⑤). Movies carry their own Year/ImdbId; episodes resolve
/// everything through <c>Season→Show</c> in one Include (design point 1 —
/// no per-episode redundancy, no N+1). The three file hashes are computed
/// lazily through <see cref="LazyMediaHasher"/> and only when the round
/// actually queries a source that consumes them (OpenSubtitles needs the
/// moviehash, shooter the segmented filehash, xunlei the sampled SHA1) — a
/// round without those sources does zero hash IO.
/// </summary>
public sealed class SubtitleSearchRequestBuilder
{
    /// <summary>Source keys whose queries consume the OpenSubtitles moviehash.</summary>
    public const string OpenSubtitlesProviderKey = "opensubtitles";

    /// <summary>Source key whose query consumes the shooter segmented filehash.</summary>
    public const string ShooterProviderKey = "shooter";

    /// <summary>Source key whose query consumes the xunlei sampled filehash.</summary>
    public const string XunleiProviderKey = "xunlei";

    /// <summary>
    /// B04: source key whose query needs the media FILE and its audio
    /// languages (whisper transcribes a clip of the container). Gated like
    /// the file hashes — rounds without the source pay nothing.
    /// </summary>
    public const string WhisperProviderKey = "whisper";

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly LazyMediaHasher _hasher;

    public SubtitleSearchRequestBuilder(
        LingarrDbContext dbContext,
        ISettingService settingService,
        LazyMediaHasher hasher)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _hasher = hasher;
    }

    /// <summary>
    /// Builds the search request for one media item.
    /// <paramref name="providersToQuery"/> is the set of source keys the
    /// caller will actually query this round — it gates hash computation
    /// (⑨ lazy hashing). <paramref name="languagesOverride"/> replaces the
    /// target∪source default (the manual search endpoint passes the user's
    /// picks); null falls back to the settings union.
    /// <paramref name="query"/> is the caller's explicit search keyword
    /// (manual-search "Query" box): when non-blank it replaces the derived
    /// library title as the search keyword, while the year/season/episode
    /// context (and release name, hashes, languages) stay resolved from the
    /// library entity.
    /// </summary>
    /// <returns>Null when the media item does not exist.</returns>
    public async Task<BuiltSubtitleSearchRequest?> BuildAsync(
        MediaType mediaType,
        int mediaId,
        IReadOnlyCollection<string>? providersToQuery,
        IReadOnlyCollection<string>? languagesOverride = null,
        CancellationToken cancellationToken = default,
        string? query = null)
    {
        string title;
        int? year;
        int? season;
        int? episode;
        string? releaseName;
        string? imdbId;
        string? mediaFilePath;
        string[]? audioLanguages;

        if (mediaType == MediaType.Movie)
        {
            var movie = await _dbContext.Movies
                .Where(m => m.Id == mediaId)
                .Select(m => new
                {
                    m.Title, m.Year, m.FileName, m.Path, m.ImdbId, m.AudioLanguages
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (movie is null)
            {
                return null;
            }

            title = movie.Title;
            year = movie.Year;
            season = null;
            episode = null;
            releaseName = movie.FileName;
            imdbId = movie.ImdbId;
            mediaFilePath = ResolveMediaFile(movie.Path, movie.FileName);
            audioLanguages = movie.AudioLanguages;
        }
        else if (mediaType == MediaType.Episode)
        {
            // Design point 1: Year/ImdbId live on Show — one Include, one
            // SQL, no per-episode columns.
            var episodeEntity = await _dbContext.Episodes
                .Where(e => e.Id == mediaId)
                .Select(e => new
                {
                    e.EpisodeNumber,
                    e.FileName,
                    e.Path,
                    e.AudioLanguages,
                    SeasonNumber = e.Season.SeasonNumber,
                    ShowTitle = e.Season.Show.Title,
                    ShowYear = e.Season.Show.Year,
                    ShowImdbId = e.Season.Show.ImdbId
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (episodeEntity is null)
            {
                return null;
            }

            title = episodeEntity.ShowTitle;
            year = episodeEntity.ShowYear;
            season = episodeEntity.SeasonNumber;
            episode = episodeEntity.EpisodeNumber;
            releaseName = episodeEntity.FileName;
            imdbId = episodeEntity.ShowImdbId;
            mediaFilePath = ResolveMediaFile(episodeEntity.Path, episodeEntity.FileName);
            audioLanguages = episodeEntity.AudioLanguages;
        }
        else
        {
            return null;
        }

        var languages = languagesOverride ?? await GetLanguageUnionAsync(cancellationToken);
        if (languages.Count == 0)
        {
            // No configured languages and no override — nothing to search
            // for. Honest empty rather than an empty request.
            return null;
        }

        var queryProviders = providersToQuery ?? [];
        var videoHash = ContainsKey(queryProviders, OpenSubtitlesProviderKey)
            ? _hasher.VideoHash(mediaFilePath)
            : null;
        var shooterHash = ContainsKey(queryProviders, ShooterProviderKey)
            ? _hasher.ShooterHash(mediaFilePath)
            : null;
        var xunleiHash = ContainsKey(queryProviders, XunleiProviderKey)
            ? _hasher.XunleiHash(mediaFilePath)
            : null;
        // B04: the whisper source consumes the container file itself and its
        // audio languages — filled only when a whisper round asks for them
        // (the hash-style lazy gate; the two columns were projected anyway).
        var whisperRound = ContainsKey(queryProviders, WhisperProviderKey);

        // QA "RequestBuilder 接 query" + R3b refinement: an explicit keyword
        // replaces the derived title as the search keyword (providers keyword
        // off request.Title today, so the replacement is what they search).
        // A trailing SxxEyy/NxMM tag ("Show Name S01E01" — the front end
        // pre-fills that shape) is stripped first via the scorer's own tag
        // grammar, so the keyword stays equivalent to the default derived
        // request (Title="Show Name" + structured S01E01) on every provider —
        // Gestdown's exact show-name lookup is not degraded by the suffix.
        // The tag's numbers are a FALLBACK only: the library's structured
        // season/episode win whenever they exist, and a movie request never
        // gains episode fields from its keyword (that would turn a movie
        // query into an episode one).
        // The `Query:` argument mirrors the effective keyword on the
        // contracted field of the orchestrator request object (string?
        // Query) — added by the parallel execution-chain batch; this call is
        // the agreed consumer.
        string? effectiveQuery = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var (keyword, tag) = Scoring.SubtitleMatchScorer.SplitTrailingSeasonEpisode(query.Trim());
            title = keyword;
            effectiveQuery = keyword;
            if (mediaType == MediaType.Episode && tag is { } parsed
                && season is null && episode is null)
            {
                season = parsed.Season;
                episode = parsed.Episode;
            }
        }

        return new BuiltSubtitleSearchRequest(
            new SubtitleSearchRequest(
                title,
                year,
                season,
                episode,
                releaseName,
                videoHash,
                [.. languages],
                imdbId,
                shooterHash,
                xunleiHash,
                Query: effectiveQuery,
                MediaFilePath: whisperRound ? mediaFilePath : null,
                AudioLanguages: whisperRound ? audioLanguages : null),
            mediaFilePath);
    }

    /// <summary>
    /// The default language set of a search: translation targets ∪ source
    /// whitelist (design ②). A subtitle in either set is worth finding —
    /// targets directly, sources as translation feedstock (⑦ step ⑦).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLanguageUnionAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages);
        var sources = await _settingService.GetSettingAsJson<SourceLanguage>(
            SettingKeys.Translation.SourceLanguages);

        var union = new List<string>();
        foreach (var code in (targets ?? []).Select(l => l.Code)
                     .Concat((sources ?? []).Select(l => l.Code)))
        {
            if (!string.IsNullOrWhiteSpace(code)
                && !union.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                union.Add(code);
            }
        }

        return union;
    }

    /// <summary>
    /// Episodes and movies store the directory in Path and the video file
    /// name in FileName (EpisodeSync/MovieSync write-side contract) — the
    /// hashable media file is their combination.
    /// </summary>
    private static string? ResolveMediaFile(string? directory, string? fileName)
        => string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(directory, fileName);

    private static bool ContainsKey(IReadOnlyCollection<string> providers, string key)
        => providers.Contains(key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The built request plus the resolved media file path — the pipeline needs
/// the latter for the naming template even when no hash was computed.
/// </summary>
/// <param name="Request">The provider query.</param>
/// <param name="MediaFilePath">Absolute path of the media video file; null when unknown.</param>
public sealed record BuiltSubtitleSearchRequest(
    SubtitleSearchRequest Request,
    string? MediaFilePath);
