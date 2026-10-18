using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Helpers;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class MovieSync : IMovieSync
{
    private readonly LingarrDbContext _dbContext;
    private readonly PathConversionService _pathConversionService;
    private readonly ILogger<MovieSync> _logger;
    private readonly IImageSync _imageSync;

    public MovieSync(
        LingarrDbContext dbContext,
        PathConversionService pathConversionService,
        ILogger<MovieSync> logger,
        IImageSync imageSync)
    {
        _dbContext = dbContext;
        _pathConversionService = pathConversionService;
        _logger = logger;
        _imageSync = imageSync;
    }

    /// <inheritdoc />
    public async Task<Movie?> SyncMovie(RadarrMovie movie, bool defaultInclude = true)
    {
        if (!movie.HasFile)
        {
            _logger.LogDebug("Movie '{Title}' (ID: {Id}) has no file, skipping.", movie.Title, movie.Id);
            return null;
        }

        var movieEntity = await _dbContext.Movies
            .Include(m => m.Images)
            .FirstOrDefaultAsync(m => m.RadarrId == movie.Id);

        // Radarr can report hasFile=true without a movieFile resource; the
        // path then folds to the empty-path convention, exactly like a
        // movieFile whose Path itself is null (Quality/Size stay null the
        // same way via their own null-propagating reads).
        var moviePath = _pathConversionService.ConvertAndMapPath(
            movie.MovieFile?.Path ?? string.Empty,
            MediaType.Movie
        );

        var audioLanguages = ResolveAudioLanguages(movie.MovieFile);
        // option B: TMDb original language as a guess-level hint — name→code
        // via the shared normalizer (the id is *arr-internal, not ISO)
        var suggested = movie.OriginalLanguage is { } original
            ? EmbeddedTrackService.NormalizeLanguage(original.Name)
            : null;

        if (movieEntity == null)
        {
            movieEntity = new Movie
            {
                RadarrId = movie.Id,
                Title = movie.Title,
                DateAdded = UtcDateTime.Parse(movie.Added),
                FileName = Path.GetFileNameWithoutExtension(moviePath),
                Path = Path.GetDirectoryName(moviePath) ?? string.Empty,
                IncludeInTranslation = defaultInclude,
                Year = movie.Year,
                ImdbId = movie.ImdbId,
                Quality = movie.MovieFile?.Quality?.Quality?.Name,
                SizeBytes = movie.MovieFile?.Size,
                ImdbRating = ResolveImdbRating(movie.Ratings),
                Overview = movie.Overview,
                VideoCodec = movie.MovieFile?.MediaInfo?.VideoCodec,
                AudioCodec = movie.MovieFile?.MediaInfo?.AudioCodec,
                AudioChannels = movie.MovieFile?.MediaInfo?.AudioChannels,
                Resolution = movie.MovieFile?.MediaInfo?.Resolution,
                RuntimeMinutes = movie.MovieFile?.MediaInfo?.RuntimeMinutes,
                SuggestedLanguage = suggested
            };
            // The setter folds an empty result back to null — the column
            // never stores "[]" (unknown is null).
            movieEntity.AudioLanguages = audioLanguages?.ToArray();
            _dbContext.Movies.Add(movieEntity);
        }
        else
        {
            movieEntity.Title = movie.Title;
            movieEntity.DateAdded = UtcDateTime.Parse(movie.Added);
            movieEntity.FileName = Path.GetFileNameWithoutExtension(moviePath);
            movieEntity.Path = Path.GetDirectoryName(moviePath) ?? string.Empty;
            // A payload without a recognized code never clears the column —
            // an ffprobe-filled value survives until *arr has real data; a
            // manual/identified value outranks the payload entirely.
            if (audioLanguages != null && movieEntity.AudioLanguagesManual != true)
            {
                movieEntity.AudioLanguages = audioLanguages?.ToArray();
            }
            movieEntity.SuggestedLanguage = suggested;

            // B2/M0008 metadata: Radarr always carries these fields on a
            // hasFile movie, so the payload is authoritative on re-sync —
            // unlike audio languages there is no lower-trust local source
            // whose value would need protecting.
            movieEntity.Year = movie.Year;
            movieEntity.ImdbId = movie.ImdbId;
            movieEntity.Quality = movie.MovieFile?.Quality?.Quality?.Name;
            movieEntity.SizeBytes = movie.MovieFile?.Size;
            movieEntity.ImdbRating = ResolveImdbRating(movie.Ratings);
            // Media-file detail batch: overview + mediaInfo facts follow the
            // same payload-authoritative rule.
            movieEntity.Overview = movie.Overview;
            movieEntity.VideoCodec = movie.MovieFile?.MediaInfo?.VideoCodec;
            movieEntity.AudioCodec = movie.MovieFile?.MediaInfo?.AudioCodec;
            movieEntity.AudioChannels = movie.MovieFile?.MediaInfo?.AudioChannels;
            movieEntity.Resolution = movie.MovieFile?.MediaInfo?.Resolution;
            movieEntity.RuntimeMinutes = movie.MovieFile?.MediaInfo?.RuntimeMinutes;
        }

        _logger.LogInformation("Syncing movie: {MovieId} with Path: {Path}", movie.Id, movieEntity.Path);

        if (movie.Images?.Any() == true)
        {
            _imageSync.SyncImages(movieEntity.Images, movie.Images);
        }

        return movieEntity;
    }

    /// <summary>
    /// PRD card A trust chain: mediaInfo.audioLanguages (ffprobe truth,
    /// "english / chi") outranks the languages aggregate ([{id,name}] guess).
    /// Returns null when neither source carries a recognized code — the
    /// column then stays null ("unknown") so the embedded-scan ffprobe
    /// piggyback can fill it later.
    /// </summary>
    private static List<string>? ResolveAudioLanguages(RadarrMovieFile? movieFile)
    {
        var codes = EmbeddedTrackService.ParseAudioLanguageString(movieFile?.MediaInfo?.AudioLanguages);
        if (codes.Count == 0)
        {
            // null ELEMENTS are a real payload shape (*arr serves [null] for
            // unknown languages) and must never reach .Name
            codes = EmbeddedTrackService.NormalizeLanguageNames(
                (movieFile?.Languages ?? []).Where(language => language != null)
                    .Select(language => language!.Name));
        }

        return codes.Count > 0 ? codes : null;
    }

    /// <summary>
    /// B2: Radarr exposes the IMDb score as ratings.imdb = {votes, value};
    /// votes &lt;= 1 means *arr has no real user aggregate yet (value 0), so
    /// the column stays null instead of storing a fake 0.
    /// </summary>
    private static double? ResolveImdbRating(RadarrRatings? ratings)
    {
        return ratings?.Imdb is { Votes: > 0, Value: not null } imdb ? imdb.Value : null;
    }
}