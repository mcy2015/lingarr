using FluentMigrator.Runner;
using Lingarr.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lingarr.Migrations.Tests;

/// <summary>
/// SQLite-only migration tests around the single squashed baseline
/// (M0001_Baseline). The baseline was verified column-by-column against a
/// database built by the original 29-migration chain during the squash; these
/// tests lock that shape in and prove the Up/Down cycle works.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationTests
{
    [Fact]
    public async Task Sqlite_BaselineMigratesFreshDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // The baseline plus the auth-sessions, schedule-cron,
            // embedded-track, consume-embedded, performance-index,
            // audio-and-language-cache, subtitle-search,
            // subtitle-search-telemetry, subtitle-search-schema-cleanup,
            // video-hash-and-title-fts,
            // suggested-language-and-manual-audio, media-file-info and
            // subtitle-upgrade-settings migrations are applied.
            Assert.Equal(14L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM version_info"));
            Assert.Equal(14L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // Seed data is present.
            Assert.Equal(100L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'navigate_to_details_on_request' AND value = 'true'"));

            // M0003 seeds the cron rows for the two built-ins that used to
            // run on hardcoded fallbacks, at exactly those fallback values.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'cleanup_schedule' AND value = '0 0 * * 0'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'statistics_schedule' AND value = '0 0 * * *'"));

            // M0004 seeds the embedded track scan cron at its daily 04:30 default.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'embedded_track_schedule' AND value = '30 4 * * *'"));

            // M0005 seeds the F2b automation opt-in off by default.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'consume_embedded_tracks' AND value = 'false'"));

            // Fresh installs ship with authentication enabled (secure default).
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'auth_enabled' AND value = 'true'"));

            // M0002 seeds the forced-change flag off (a fresh default
            // admin/admin seed flips it at startup).
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'must_change_password' AND value = 'false'"));

            // M0008 seeds the subtitle-search feature rows: off by default,
            // daily 05:00 round (one hour after the 04:00 sync wave), the
            // PM-decided single auto-score threshold of 75, SRT conversion
            // and auto-translate on, 200 media per round.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_enabled' AND value = 'false'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_schedule' AND value = '0 5 * * *'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_auto_score' AND value = '75'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_convert_srt' AND value = 'true'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_batch_limit' AND value = '200'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_auto_translate' AND value = 'true'"));

            // M0014 seeds the subtitle-upgrade feature rows: off by default
            // (replacing stored files never runs implicitly), daily 04:00
            // cron inside the maintenance wave, margin 3 and window 14 days.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_upgrade_enabled' AND value = 'false'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_upgrade_schedule' AND value = '0 4 * * *'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_upgrade_score_margin' AND value = '3'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_upgrade_window_days' AND value = '14'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_BaselineRollsBackFullyAndReapplies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            // Roll all the way down: every application table must be gone.
            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(0);
            }

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                // MigrateDown(0) removes every application table; only the
                // FluentMigrator bookkeeping table survives.
                var remaining = await ReadColumnAsync(connection,
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", columnIndex: 0);
                Assert.Equal(["version_info"], actual: remaining);
            }

            // Up again from the empty database must fully rebuild schema and seeds.
            MigrationConfiguration.RunMigrations(BuildRunner(connectionString));

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                // 18 tables through M0007, +3 business tables in M0008
                // (subtitle_search_state, subtitle_downloads,
                // subtitle_search_blacklist) and +3 telemetry tables in
                // M0009 (subtitle_search_sessions, provider_quota_usage,
                // coverage_daily). M0010 only alters existing tables (drops
                // the dead download columns, adds the blacklist external_id)
                // and M0011 adds video_hash columns plus the FTS5 virtual
                // table — which enters sqlite_master alongside its five
                // shadow tables (media_title_fts plus _data/_idx/_content/
                // _docsize/_config), so the count goes 24 → 30.
                Assert.Equal(30L, await ExecuteScalarAsync(connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
                Assert.Equal(1L, await ExecuteScalarAsync(connection,
                    "SELECT COUNT(*) FROM settings WHERE key = 'navigate_to_details_on_request'"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0002 must be reversible on its own: rolling it back drops
    /// auth_sessions and the must_change_password seed, leaving the baseline
    /// state.
    /// </summary>
    [Fact]
    public async Task Sqlite_AuthSessionsMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(1);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Back to the baseline version; auth_sessions and the flag row are gone.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'auth_sessions'"));
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'must_change_password'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0006 only adds indexes, so rolling it back must return exactly the
    /// M0005 state: version back to 5 and every one of the eight indexes
    /// gone from sqlite_master.
    /// </summary>
    [Fact]
    public async Task Sqlite_PerformanceIndexesMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(5);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(5L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN (" +
                "'ix_translation_requests_media_type_media_id'," +
                "'ix_translation_requests_status'," +
                "'ix_translation_requests_completed_at'," +
                "'ix_translation_requests_created_at'," +
                "'ux_movies_radarr_id'," +
                "'ux_shows_sonarr_id'," +
                "'ux_episodes_sonarr_id'," +
                "'ix_daily_statistics_date')"));
            // The pre-existing baseline indexes survive the rollback.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name = 'ix_episodes_season_id'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0007 adds two columns per media table plus the sniff-cache table, so
    /// rolling it back must return exactly the M0006 state: version back to 6,
    /// the audio columns gone from movies/episodes (pre-existing columns
    /// untouched) and subtitle_language_cache dropped with its autoindex.
    /// </summary>
    [Fact]
    public async Task Sqlite_AudioAndLanguageCacheMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(6);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(6L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // The sniff-cache table (and its TEXT-PK autoindex) is gone.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'subtitle_language_cache'"));

            // The audio columns are gone from both media tables while the
            // pre-M0007 columns survive.
            var movieColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"movies\")", columnIndex: 1);
            Assert.DoesNotContain("audio_languages", movieColumns);
            Assert.DoesNotContain("audio_override", movieColumns);
            Assert.DoesNotContain("needs_translation", movieColumns);
            Assert.DoesNotContain("no_subtitle_needed", movieColumns);
            Assert.Contains("subtitle_coverage_json", movieColumns);

            var episodeColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"episodes\")", columnIndex: 1);
            Assert.DoesNotContain("audio_languages", episodeColumns);
            Assert.DoesNotContain("audio_override", episodeColumns);
            Assert.DoesNotContain("needs_translation", episodeColumns);
            Assert.DoesNotContain("no_subtitle_needed", episodeColumns);
            Assert.Contains("include_in_translation", episodeColumns);

            // M0006's indexes survive the rollback.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name = 'ux_movies_radarr_id'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0008 adds three business tables, the search/B2 columns on
    /// movies/shows and six settings rows, so rolling it back must return
    /// exactly the M0007 state: version back to 7, the three tables gone
    /// with all their indexes (including the raw-DDL partial index), the
    /// new columns gone while the M0007 audio columns survive, and the six
    /// seeded settings rows removed.
    /// </summary>
    [Fact]
    public async Task Sqlite_SubtitleSearchMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(7);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(7L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // The three business tables and every one of their indexes are
            // gone (boost is a raw-DDL partial index, so DROP INDEX IF EXISTS).
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name IN (" +
                "'subtitle_search_state'," +
                "'subtitle_downloads'," +
                "'subtitle_search_blacklist'," +
                "'ix_subtitle_search_state_dequeue'," +
                "'ux_subtitle_search_state_media'," +
                "'ix_subtitle_search_state_boost'," +
                "'ix_subtitle_downloads_media'," +
                "'ix_subtitle_downloads_created'," +
                "'ix_subtitle_downloads_source')"));

            // The M0008 columns are gone from movies/shows while the M0007
            // columns survive untouched.
            var movieColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"movies\")", columnIndex: 1);
            Assert.DoesNotContain("year", movieColumns);
            Assert.DoesNotContain("imdb_id", movieColumns);
            Assert.DoesNotContain("quality", movieColumns);
            Assert.DoesNotContain("size_bytes", movieColumns);
            Assert.DoesNotContain("imdb_rating", movieColumns);
            Assert.Contains("audio_languages", movieColumns);

            var showColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"shows\")", columnIndex: 1);
            Assert.DoesNotContain("year", showColumns);
            Assert.DoesNotContain("imdb_id", showColumns);
            Assert.DoesNotContain("imdb_rating", showColumns);

            // The six seeded settings rows are removed and nothing else.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key LIKE 'subtitle_search_%'"));
            Assert.Equal(90L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings"));

            // M0007's table and M0006's indexes survive the rollback.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'subtitle_language_cache'"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name = 'ux_movies_radarr_id'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0009 adds three telemetry tables and the coverage watermark columns,
    /// so rolling it back must return exactly the M0008 state: version back
    /// to 8, the telemetry tables gone (the composite-PK autoindexes with
    /// them), coverage_checked_at gone from movies/episodes while every
    /// M0008 table, column and settings row survives.
    /// </summary>
    [Fact]
    public async Task Sqlite_SubtitleSearchTelemetryMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(8);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(8L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // The telemetry tables (and their composite-PK autoindexes) are
            // gone from sqlite_master entirely.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name IN (" +
                "'subtitle_search_sessions'," +
                "'provider_quota_usage'," +
                "'coverage_daily'," +
                "'ix_subtitle_search_sessions_media'," +
                "'ix_subtitle_search_sessions_created'," +
                "'sqlite_autoindex_provider_quota_usage_1'," +
                "'sqlite_autoindex_coverage_daily_1')"));

            // The watermark column is gone from both media tables while the
            // M0008 columns survive.
            var movieColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"movies\")", columnIndex: 1);
            Assert.DoesNotContain("coverage_checked_at", movieColumns);
            Assert.Contains("quality", movieColumns);
            Assert.Contains("imdb_rating", movieColumns);

            var episodeColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"episodes\")", columnIndex: 1);
            Assert.DoesNotContain("coverage_checked_at", episodeColumns);

            // M0008's business tables and settings rows survive the rollback.
            Assert.Equal(3L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (" +
                "'subtitle_search_state'," +
                "'subtitle_downloads'," +
                "'subtitle_search_blacklist')"));
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_enabled' AND value = 'false'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0010 drops the never-written replacement-chain columns from
    /// subtitle_downloads and adds the blacklist external_id key with its
    /// lookup index, so rolling it back must return exactly the M0009 state:
    /// version back to 9, both dead columns restored (nullable, no data —
    /// nothing ever wrote them), external_id and its index gone.
    /// </summary>
    [Fact]
    public async Task Sqlite_SubtitleSearchSchemaCleanupMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(9);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(9L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // The dead columns are back on subtitle_downloads, nullable and
            // data-free, while the columns M0008 created around them survive.
            var downloadColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"subtitle_downloads\")", columnIndex: 1);
            Assert.Contains("removed_at", downloadColumns);
            Assert.Contains("replaced_by_download_id", downloadColumns);
            Assert.Contains("search_session_id", downloadColumns);
            Assert.Contains("created_at", downloadColumns);

            // The blacklist key column and its lookup index are gone; the
            // M0008 columns survive.
            var blacklistColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"subtitle_search_blacklist\")", columnIndex: 1);
            Assert.DoesNotContain("external_id", blacklistColumns);
            Assert.Equal(["id", "provider", "reason", "expires_at"], blacklistColumns);
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_subtitle_search_blacklist_provider'"));

            // The download-history indexes survive the rollback untouched.
            Assert.Equal(3L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_subtitle_downloads_%'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Locks in the schema shape the squash verified against the old
    /// 29-migration chain: tables, column names in order, foreign keys and
    /// explicit indexes. Fails when the baseline drifts from the contract the
    /// historical chain left behind.
    /// </summary>
    [Fact]
    public async Task Sqlite_BaselineSchemaMatchesLegacyChainShape()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            MigrationConfiguration.RunMigrations(BuildRunner(connectionString));

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            var expectedColumns = new Dictionary<string, string[]>
            {
                ["settings"] = ["key", "value", "provider"],
                ["movies"] = ["id", "radarr_id", "title", "file_name", "path", "media_hash", "date_added",
                    "translation_age_threshold", "created_at", "updated_at", "include_in_translation",
                    "subtitle_coverage_json", "audio_languages", "audio_override", "needs_translation", "no_subtitle_needed",
                    // M0008 search-request keys + B2 metadata, M0009 watermark.
                    "year", "imdb_id", "quality", "size_bytes", "imdb_rating", "coverage_checked_at",
                    // M0011: dedicated OpenSubtitles-moviehash column (media_hash
                    // stays owned by the translation pipeline).
                    "video_hash",
                    // M0012: TMdb original-language guess + manual provenance.
                    "suggested_language", "audio_languages_manual",
                    // M0013 media-file detail: overview + mediaInfo facts.
                    "overview", "video_codec", "audio_codec", "audio_channels", "resolution",
                    "runtime_minutes"],
                ["shows"] = ["id", "sonarr_id", "title", "path", "date_added", "translation_age_threshold",
                    "created_at", "updated_at", "include_in_translation", "subtitle_coverage_json",
                    // M0008: series-level year/imdb (design point 1) + B2 rating.
                    "year", "imdb_id", "imdb_rating",
                    // M0012: series-level original-language guess.
                    "suggested_language",
                    // M0013 media-file detail: series overview.
                    "overview"],
                ["seasons"] = ["id", "season_number", "path", "show_id", "created_at", "updated_at",
                    "include_in_translation"],
                ["episodes"] = ["id", "sonarr_id", "episode_number", "title", "file_name", "path", "media_hash",
                    "season_id", "date_added", "created_at", "updated_at", "include_in_translation",
                    "audio_languages", "audio_override", "needs_translation", "no_subtitle_needed",
                    // M0009 coverage watermark (consumed by the M7 batch).
                    "coverage_checked_at",
                    // M0011: dedicated OpenSubtitles-moviehash column.
                    "video_hash",
                    // M0012: manual/identified audio provenance (episodes are
                    // the rows the sync actually writes).
                    "audio_languages_manual",
                    // M0013 media-file detail: per-episode file facts.
                    "quality", "size_bytes", "video_codec", "audio_codec", "audio_channels",
                    "resolution", "runtime_minutes"],
                ["images"] = ["id", "type", "path", "show_id", "movie_id"],
                ["path_mappings"] = ["id", "source_path", "destination_path", "media_type", "created_at", "updated_at"],
                ["translation_requests"] = ["id", "job_id", "media_id", "title", "source_language", "target_language",
                    "subtitle_to_translate", "translated_subtitle", "media_type", "status", "completed_at",
                    "created_at", "updated_at", "error_message", "stack_trace", "job_type", "is_bilingual", "source",
                    "content_hash", "service_type", "progress", "started_at", "retry_count", "characters_translated"],
                ["translation_request_events"] = ["id", "translation_request_id", "status", "message", "created_at",
                    "updated_at", "event_type", "provider"],
                ["translation_request_lines"] = ["id", "translation_request_id", "position", "source", "target",
                    "created_at", "updated_at", "service", "matched_source", "matched_target", "tier"],
                ["translation_glossaries"] = ["id", "source_term", "target_term", "note", "is_locked", "created_at",
                    "updated_at"],
                ["statistics"] = ["id", "total_lines_translated", "total_files_translated",
                    "total_characters_translated", "total_movies", "total_episodes", "total_subtitles",
                    "translations_by_media_type_json", "translations_by_service_json", "subtitles_by_language_json",
                    "translations_by_model_json", "created_at", "updated_at"],
                ["daily_statistics"] = ["id", "date", "translation_count", "created_at", "updated_at"],
                ["users"] = ["id", "username", "password_hash", "created_at", "last_login_at"],
                ["auth_sessions"] = ["key", "value", "user_id", "expires_at"],
                ["media_subtitle_tracks"] = ["id", "media_type", "media_id", "stream_index", "language",
                    "raw_language", "title", "codec", "is_text_based", "file_path", "media_hash",
                    "created_at", "updated_at"],
                ["subtitle_language_cache"] = ["path", "mtime_ticks", "length", "language", "updated_at"],
                // M0008 subtitle-search business tables.
                ["subtitle_search_state"] = ["id", "media_type", "media_id", "status", "priority_rank",
                    "pinned", "webhook_boost_until", "next_attempt_at", "attempt_count", "last_error",
                    "last_searched_at", "updated_at"],
                ["subtitle_downloads"] = ["id", "media_type", "media_id", "provider", "external_id",
                    "language", "file_path", "match_score", "hash_match", "source", "search_session_id",
                    // M0010 dropped the never-written replacement-chain columns
                    // (removed_at, replaced_by_download_id).
                    "result", "error", "created_at"],
                // M0010 appended external_id (write side of the locked
                // (provider, external_id) blacklist contract).
                ["subtitle_search_blacklist"] = ["id", "provider", "reason", "expires_at", "external_id"],
                // M0009 telemetry tables.
                ["subtitle_search_sessions"] = ["id", "media_type", "media_id", "provider",
                    "query_language", "result_count", "hit_target", "created_at"],
                ["provider_quota_usage"] = ["date", "provider", "searches", "downloads",
                    "quota_limit", "quota_remaining"],
                ["coverage_daily"] = ["date", "media_type", "language", "covered", "total"],
                // M0011: the FTS5 title index. Rowids encode the media type
                // (movie = 1e12 + id, show = 2e12 + id, episode = 3e12 + id);
                // media_type is stored UNINDEXED for operator inspection.
                ["media_title_fts"] = ["title", "media_type"],
                ["version_info"] = ["version", "applied_on", "description"],
            };

            var expectedForeignKeys = new Dictionary<string, int>
            {
                ["images"] = 2,
                ["seasons"] = 1,
                ["episodes"] = 1,
                ["translation_request_events"] = 1,
                ["translation_request_lines"] = 1,
            };

            var expectedIndexes = new Dictionary<string, string[]>
            {
                // M0006: the external-id lookups are UNIQUE (sync upserts
                // match on exactly one row per Radarr/Sonarr endpoint).
                ["movies"] = ["ux_movies_radarr_id", "ix_movies_needs_translation"],
                ["shows"] = ["ux_shows_sonarr_id"],
                ["episodes"] = ["ix_episodes_season_id", "ux_episodes_sonarr_id", "ix_episodes_needs_translation"],
                ["images"] = ["ix_images_movie_id", "ix_images_show_id"],
                ["seasons"] = ["ix_seasons_show_id"],
                // The TEXT primary key adds an implicit autoindex alongside
                // the two explicit ones.
                ["auth_sessions"] =
                [
                    "ix_auth_sessions_expires_at",
                    "ix_auth_sessions_user_id",
                    "sqlite_autoindex_auth_sessions_1"
                ],
                // M0006 (P0-9): translation_requests was index-free after
                // the baseline; the polling, status-count and trend queries
                // now get these four.
                ["translation_requests"] =
                [
                    "ix_translation_requests_completed_at",
                    "ix_translation_requests_created_at",
                    "ix_translation_requests_media_type_media_id",
                    "ix_translation_requests_status"
                ],
                ["translation_glossaries"] = ["ix_glossary_source_term"],
                ["media_subtitle_tracks"] = ["ix_media_subtitle_tracks_media_type_media_id"],
                // The TEXT primary key adds an implicit autoindex, as with
                // auth_sessions.
                ["subtitle_language_cache"] = ["sqlite_autoindex_subtitle_language_cache_1"],
                // M0008: dequeue covering index, enqueue-dedup UNIQUE and
                // the webhook-boost partial index on subtitle_search_state.
                // M0010: the blacklist gains the (provider, external_id)
                // exact-match lookup index the filter side reads.
                ["subtitle_search_state"] =
                [
                    "ix_subtitle_search_state_boost",
                    "ix_subtitle_search_state_dequeue",
                    "ux_subtitle_search_state_media"
                ],
                ["subtitle_search_blacklist"] = ["ix_subtitle_search_blacklist_provider"],
                ["subtitle_downloads"] =
                [
                    "ix_subtitle_downloads_created",
                    "ix_subtitle_downloads_media",
                    "ix_subtitle_downloads_source"
                ],
                // M0009: sessions media/created indexes; the two counter
                // tables get their composite-PK autoindexes.
                ["subtitle_search_sessions"] =
                [
                    "ix_subtitle_search_sessions_created",
                    "ix_subtitle_search_sessions_media"
                ],
                ["provider_quota_usage"] = ["sqlite_autoindex_provider_quota_usage_1"],
                ["coverage_daily"] = ["sqlite_autoindex_coverage_daily_1"],
                ["translation_request_events"] = ["ix_translation_request_events_translation_request_id"],
                ["translation_request_lines"] = ["ix_translation_request_lines_translation_request_id"],
                ["daily_statistics"] = ["ix_daily_statistics_date"],
                ["version_info"] = ["ux_version"],
            };

            var tables = await ReadColumnAsync(connection,
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", columnIndex: 0);
            // The FTS5 shadow tables (media_title_fts_data/_idx/_content/
            // _docsize/_config) carry SQLite-internal column names (the
            // content mirror is c0/c1, not the declared column names) that
            // are not contract — only their existence is pinned; the exact
            // shape stays asserted on the virtual table itself.
            var expectedNonShadow = expectedColumns.Keys.Order().ToArray();
            Assert.Equal(expectedNonShadow, tables
                .Where(name => !name.StartsWith("media_title_fts_", StringComparison.Ordinal))
                .Order().ToArray());
            Assert.Contains("media_title_fts_data", tables);
            Assert.Contains("media_title_fts_idx", tables);
            Assert.Contains("media_title_fts_content", tables);
            Assert.Contains("media_title_fts_docsize", tables);
            Assert.Contains("media_title_fts_config", tables);

            foreach (var (table, columns) in expectedColumns)
            {
                // table_info columns: cid, name, type, notnull, dflt_value, pk.
                var actual = await ReadColumnAsync(connection, $"PRAGMA table_info(\"{table}\")", columnIndex: 1);
                Assert.True(actual.Count > 0, $"Table {table} is missing");
                Assert.Equal(columns, actual);
            }

            foreach (var (table, count) in expectedForeignKeys)
            {
                var foreignKeys = await ReadPragmaRowCountAsync(connection, "foreign_key_list", table);
                Assert.Equal(count, foreignKeys);
            }

            foreach (var (table, indexes) in expectedIndexes)
            {
                // index_list columns: seq, name, unique, origin, partial.
                var actual = await ReadColumnAsync(connection, $"PRAGMA index_list(\"{table}\")", columnIndex: 1);
                Assert.Equal(indexes.Order().ToArray(), actual.Order().ToArray());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0011 adds video_hash to both media tables and creates the FTS5 title
    /// index, so rolling it back must return exactly the M0010 state: version
    /// back to 10, the column gone from movies/episodes while media_hash
    /// survives untouched, and the virtual table plus all five shadow tables
    /// gone from sqlite_master (DROP TABLE on the vtab cascades).
    /// </summary>
    [Fact]
    public async Task Sqlite_VideoHashAndTitleFtsMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(10);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(10L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // video_hash is gone from both media tables; media_hash (the
            // translation pipeline's column) survives the rollback.
            var movieColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"movies\")", columnIndex: 1);
            Assert.DoesNotContain("video_hash", movieColumns);
            Assert.Contains("media_hash", movieColumns);
            var episodeColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"episodes\")", columnIndex: 1);
            Assert.DoesNotContain("video_hash", episodeColumns);
            Assert.Contains("media_hash", episodeColumns);

            // The virtual table and every shadow table are gone entirely.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'media_title_fts%'"));

            // M0010's artifacts survive the rollback.
            var blacklistColumns = await ReadColumnAsync(connection,
                "PRAGMA table_info(\"subtitle_search_blacklist\")", columnIndex: 1);
            Assert.Contains("external_id", blacklistColumns);
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_subtitle_search_blacklist_provider'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// M0014 only seeds four settings rows, so rolling it back must return
    /// exactly the M0013 state: version back to 13, the four
    /// subtitle_upgrade rows removed, nothing else touched — every table,
    /// column and pre-existing settings row survives.
    /// </summary>
    [Fact]
    public async Task Sqlite_SubtitleUpgradeSettingsMigration_RollsBackIndependently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var serviceProvider = BuildRunner(connectionString);
            MigrationConfiguration.RunMigrations(serviceProvider);

            using (var scope = serviceProvider.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(13);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            Assert.Equal(13L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // The four seeded rows are gone and nothing else: back to the
            // M0013 settings count, and no subtitle_upgrade key of any value.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key LIKE 'subtitle_upgrade_%'"));
            Assert.Equal(96L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings"));

            // M0008's feature rows survive untouched (the upgrade feature
            // reads subtitle_search_auto_score at round time).
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM settings WHERE key = 'subtitle_search_auto_score' AND value = '75'"));

            // No schema artifact existed to remove — every table survives.
            Assert.Equal(30L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// The FTS5 engine gate (audit P1-4): SQLitePCLraw's bundled e_sqlite3
    /// has FTS5 compiled in (FTS5 ships enabled in every e_sqlite3 build
    /// since 3.9; bundle 2.1.x carries 3.44+), and the migration creating
    /// the virtual table already fails the suite outright if it were not.
    /// This test pins the query semantics the library search builds on:
    /// quoted word-prefix phrases, unicode61 case folding, mid-word
    /// non-matching, and the rowid type ranges.
    /// </summary>
    [Fact]
    public async Task Sqlite_TitleSearchFts_MatchesWordPrefixes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            MigrationConfiguration.RunMigrations(BuildRunner(connectionString));

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await ExecuteAsync(connection,
                "INSERT INTO media_title_fts(rowid, title, media_type) VALUES " +
                "(1000000000001, 'The Matrix Reloaded', 'movie'), " +
                "(1000000000002, 'Matrix Revolutions', 'movie'), " +
                "(2000000000001, 'Foundation', 'show')");

            // Word prefix, either case — unicode61 folds case on both sides.
            Assert.Equal(2L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"mat\"*'"));
            Assert.Equal(2L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"MAT\"*'"));

            // A prefix matches any token of the title, not only the first word.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"relo\"*'"));

            // Mid-word fragments do not match — the locked prefix semantics
            // that replace the old substring LIKE.
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"trix\"*'"));

            // Multi-token queries AND their prefix phrases.
            Assert.Equal(1L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"matrix\" AND \"revo\"*'"));

            // The rowid type range narrows a match to one media type
            // (movie range: both Matrix rows; show range: none).
            Assert.Equal(2L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"mat\"*' " +
                "AND rowid >= 1000000000000 AND rowid < 1214748364800"));
            Assert.Equal(0L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM media_title_fts WHERE media_title_fts MATCH '\"mat\"*' " +
                "AND rowid >= 2000000000000 AND rowid < 2214748364800"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static IServiceProvider BuildRunner(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddFluentMigrator(connectionString);
        return services.BuildServiceProvider();
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ReadPragmaRowCountAsync(SqliteConnection connection, string pragma, string table)
    {
        // Table-valued PRAGMA functions take the object name as a string literal.
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_{pragma}('{table}')";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<string>> ReadColumnAsync(
        SqliteConnection connection,
        string commandText,
        int columnIndex)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.IsDBNull(columnIndex) ? "<null>" : Convert.ToString(reader.GetValue(columnIndex)));
        }
        return values;
    }
}
