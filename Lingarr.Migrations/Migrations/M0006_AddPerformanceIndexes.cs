using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Performance indexes from the 100k-library scale audit
/// (docs/scale-audit-100k.zh-CN.md P0-9, P1-1, P1-9).
///
/// translation_requests was left with zero indexes by the baseline: the
/// 30s-status polling (status/media filters), the status-count GROUP BY
/// (P1-9), the trend window over completed requests (P1-10) and the
/// created_at-ordered history pagination all degenerated into full table
/// scans on the largest table in the database.
///
/// The three external-id lookups (movies.radarr_id, shows.sonarr_id,
/// episodes.sonarr_id) back the sync upserts (MovieSync/ShowSync find by
/// external id) and the webhook resolvers (MediaService). They are UNIQUE:
/// every write path is find-by-external-id-then-insert against a single
/// Radarr/Sonarr endpoint, so the ids are semantically unique and the
/// columns are NOT NULL. The constraint also catches webhook/sync races
/// that would otherwise silently duplicate rows and make the
/// FirstOrDefault lookups nondeterministic.
///
/// daily_statistics.date serves the trend range scan and the
/// get-or-create point lookup hit on every recorded translation.
/// </summary>
[Migration(6)]
public class M0006_AddPerformanceIndexes : Migration
{
    public override void Up()
    {
        // P0-9: translation_requests carried no index at all after M0001.
        // Media detail history filters by (media_type, media_id); the shape
        // mirrors ix_media_subtitle_tracks_media_type_media_id.
        Create.Index("ix_translation_requests_media_type_media_id")
            .OnTable("translation_requests")
            .OnColumn("media_type").Ascending()
            .OnColumn("media_id").Ascending();

        // P1-9: GetStatusCounts GROUP BY status plus the status equality
        // filters in the polling endpoints.
        Create.Index("ix_translation_requests_status")
            .OnTable("translation_requests")
            .OnColumn("status");

        // P1-10: trend endpoint scans the completed_at window; also serves
        // cleanup retention and completed_at-ordered listings.
        Create.Index("ix_translation_requests_completed_at")
            .OnTable("translation_requests")
            .OnColumn("completed_at");

        // Default history ordering (pagination sorts on created_at).
        Create.Index("ix_translation_requests_created_at")
            .OnTable("translation_requests")
            .OnColumn("created_at");

        // P1-1: sync upserts and webhook resolvers look media up by the
        // external id (MovieSync.cs / ShowSync.cs / MediaService.cs).
        Create.Index("ux_movies_radarr_id")
            .OnTable("movies")
            .OnColumn("radarr_id")
            .Unique();
        Create.Index("ux_shows_sonarr_id")
            .OnTable("shows")
            .OnColumn("sonarr_id")
            .Unique();
        Create.Index("ux_episodes_sonarr_id")
            .OnTable("episodes")
            .OnColumn("sonarr_id")
            .Unique();

        // Trend range scan plus the per-translation get-or-create lookup.
        Create.Index("ix_daily_statistics_date")
            .OnTable("daily_statistics")
            .OnColumn("date");
    }

    public override void Down()
    {
        Delete.Index("ix_daily_statistics_date").OnTable("daily_statistics");
        Delete.Index("ux_episodes_sonarr_id").OnTable("episodes");
        Delete.Index("ux_shows_sonarr_id").OnTable("shows");
        Delete.Index("ux_movies_radarr_id").OnTable("movies");
        Delete.Index("ix_translation_requests_created_at").OnTable("translation_requests");
        Delete.Index("ix_translation_requests_completed_at").OnTable("translation_requests");
        Delete.Index("ix_translation_requests_status").OnTable("translation_requests");
        Delete.Index("ix_translation_requests_media_type_media_id").OnTable("translation_requests");
    }
}
