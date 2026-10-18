using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Helpers;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Sync;

public class ShowSync : IShowSync
{
    private readonly LingarrDbContext _dbContext;
    private readonly IImageSync _imageSync;

    public ShowSync(
        LingarrDbContext dbContext,
        IImageSync imageSync)
    {
        _dbContext = dbContext;
        _imageSync = imageSync;
    }

    /// <inheritdoc />
    public async Task<Show> SyncShow(SonarrShow sonarrShow, bool defaultInclude = true)
    {
        var showEntity = await _dbContext.Shows
            .Include(s => s.Images)
            .Include(s => s.Seasons)
            .FirstOrDefaultAsync(s => s.SonarrId == sonarrShow.Id);

        if (showEntity == null)
        {
            showEntity = new Show
            {
                SonarrId = sonarrShow.Id,
                Title = sonarrShow.Title,
                Path = sonarrShow.Path,
                DateAdded = !string.IsNullOrEmpty(sonarrShow.Added) ? UtcDateTime.Parse(sonarrShow.Added) : DateTimeOffset.UtcNow,
                IncludeInTranslation = defaultInclude,
                Year = sonarrShow.Year,
                ImdbId = sonarrShow.ImdbId,
                ImdbRating = ResolveImdbRating(sonarrShow.Ratings),
                // option B: series original language as the guess-level hint
                SuggestedLanguage = sonarrShow.OriginalLanguage is { } original
                    ? EmbeddedTrackService.NormalizeLanguage(original.Name)
                    : null
            };
            _dbContext.Shows.Add(showEntity);
        }
        else
        {
            showEntity.Title = sonarrShow.Title;
            showEntity.Path = sonarrShow.Path;
            showEntity.DateAdded = !string.IsNullOrEmpty(sonarrShow.Added) ? UtcDateTime.Parse(sonarrShow.Added) : DateTimeOffset.UtcNow;
            // M0008/B2 metadata: Sonarr always carries year/imdbId on the
            // series resource, so the payload is authoritative on re-sync
            // (same rule as MovieSync — no lower-trust local source exists).
            showEntity.Year = sonarrShow.Year;
            showEntity.ImdbId = sonarrShow.ImdbId;
            showEntity.ImdbRating = ResolveImdbRating(sonarrShow.Ratings);
            showEntity.SuggestedLanguage = sonarrShow.OriginalLanguage is { } original
                ? EmbeddedTrackService.NormalizeLanguage(original.Name)
                : null;
        }

        if (sonarrShow.Images?.Any() == true)
        {
            _imageSync.SyncImages(showEntity.Images, sonarrShow.Images);
        }

        return showEntity;
    }

    /// <summary>
    /// B2 (MovieSync precedent): the IMDb score arrives as ratings.imdb =
    /// {votes, value}; votes &lt;= 1 means Sonarr has no real user aggregate
    /// yet (value 0), so the column stays null instead of storing a fake 0.
    /// </summary>
    private static double? ResolveImdbRating(RadarrRatings? ratings)
    {
        return ratings?.Imdb is { Votes: > 0, Value: not null } imdb ? imdb.Value : null;
    }
}
