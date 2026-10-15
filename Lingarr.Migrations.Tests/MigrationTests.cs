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
            // embedded-track, consume-embedded, performance-index and
            // audio-and-language-cache migrations are applied.
            Assert.Equal(7L, await ExecuteScalarAsync(connection,
                "SELECT COUNT(*) FROM version_info"));
            Assert.Equal(7L, await ExecuteScalarAsync(connection,
                "SELECT MAX(version) FROM version_info"));

            // Seed data is present.
            Assert.Equal(90L, await ExecuteScalarAsync(connection,
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
                Assert.Equal(18L, await ExecuteScalarAsync(connection,
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
                    "subtitle_coverage_json", "audio_languages", "audio_override", "needs_translation", "no_subtitle_needed"],
                ["shows"] = ["id", "sonarr_id", "title", "path", "date_added", "translation_age_threshold",
                    "created_at", "updated_at", "include_in_translation", "subtitle_coverage_json"],
                ["seasons"] = ["id", "season_number", "path", "show_id", "created_at", "updated_at",
                    "include_in_translation"],
                ["episodes"] = ["id", "sonarr_id", "episode_number", "title", "file_name", "path", "media_hash",
                    "season_id", "date_added", "created_at", "updated_at", "include_in_translation",
                    "audio_languages", "audio_override", "needs_translation", "no_subtitle_needed"],
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
                ["translation_request_events"] = ["ix_translation_request_events_translation_request_id"],
                ["translation_request_lines"] = ["ix_translation_request_lines_translation_request_id"],
                ["daily_statistics"] = ["ix_daily_statistics_date"],
                ["version_info"] = ["ux_version"],
            };

            var tables = await ReadColumnAsync(connection,
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", columnIndex: 0);
            Assert.Equal(expectedColumns.Keys.Order().ToArray(), tables);

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
