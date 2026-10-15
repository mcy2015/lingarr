using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Subtitle search business tables (docs/subtitle-search-tech-design.zh-CN.md
/// ④, M1 batch) plus the B2 library-metadata columns that ride the same
/// release (docs/library-enhancements-prd.zh-CN.md):
///
/// - <c>subtitle_search_state</c> is the priority queue. priority_rank is a
///   write-time-frozen scalar (0 Pinned / 1 WebhookBoost / 2 Normal) so the
///   dequeue stays a single indexed ORDER BY; ux_subtitle_search_state_media
///   backs the webhook/manual enqueue UPSERT dedup, and the partial index on
///   webhook_boost_until locates expired boosts inside the dequeue batch.
/// - <c>subtitle_downloads</c> is created with its full column set (including
///   the telemetry columns source/search_session_id/removed_at/
///   replaced_by_download_id) so the M0009 telemetry migration never has to
///   alter it — no column is added later to this table.
/// - <c>subtitle_search_blacklist</c> records provider-level bans as
///   observations (⑧); nothing writes it automatically.
/// - movies/shows gain year/imdb_id (search request building, ⑤) and the B2
///   metadata columns quality/size_bytes/imdb_rating (movies) and
///   imdb_rating (shows). The optional "monitored" column from the library
///   enhancement card was skipped — no current reader; noted in the batch
///   report.
/// - Six settings rows seed the feature's defaults; the feature itself stays
///   off (subtitle_search_enabled=false) until sources land (M2).
/// </summary>
[Migration(8)]
public class M0008_AddSubtitleSearch : Migration
{
    private const string EnabledKey = "subtitle_search_enabled";
    private const string ScheduleKey = "subtitle_search_schedule";
    private const string AutoScoreKey = "subtitle_search_auto_score";
    private const string ConvertSrtKey = "subtitle_search_convert_srt";
    private const string BatchLimitKey = "subtitle_search_batch_limit";
    private const string AutoTranslateKey = "subtitle_search_auto_translate";

    public override void Up()
    {
        // Priority queue: status 0 pending / 1 succeeded / 2 failed / 3
        // skipped; rank 0 pinned / 1 webhook-boost / 2 normal.
        Create.Table("subtitle_search_state")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("media_id").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("priority_rank").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("pinned").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("webhook_boost_until").AsDateTime().Nullable()
            .WithColumn("next_attempt_at").AsDateTime().NotNullable()
            .WithColumn("attempt_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("last_error").AsCustom("TEXT").Nullable()
            .WithColumn("last_searched_at").AsDateTime().Nullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        // Dequeue covering index: WHERE status=0 AND next_attempt_at<=@now
        // ORDER BY priority_rank, next_attempt_at LIMIT @batchLimit.
        Create.Index("ix_subtitle_search_state_dequeue")
            .OnTable("subtitle_search_state")
            .OnColumn("status").Ascending()
            .OnColumn("priority_rank").Ascending()
            .OnColumn("next_attempt_at").Ascending();

        // Webhook/manual enqueue dedup (INSERT ... ON CONFLICT target).
        Create.Index("ux_subtitle_search_state_media")
            .OnTable("subtitle_search_state")
            .OnColumn("media_type").Ascending()
            .OnColumn("media_id").Unique();

        // Partial index for the in-batch webhook-boost expiry downgrade.
        // SQLite partial indexes are not expressible in FluentMigrator's
        // fluent surface (M0007 precedent for the raw DDL).
        Execute.Sql(
            "CREATE INDEX ix_subtitle_search_state_boost ON subtitle_search_state (webhook_boost_until) " +
            "WHERE webhook_boost_until IS NOT NULL");

        // Download history: created with the full column set — source
        // (0 manual / 1 auto / 2 webhook), result (0 succeeded / 1 failed /
        // 2 low-confidence skip / 3 already-exists skip) and the replacement
        // chain columns land now, not in a later ALTER.
        Create.Table("subtitle_downloads")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("media_id").AsInt32().NotNullable()
            .WithColumn("provider").AsCustom("TEXT").NotNullable()
            .WithColumn("external_id").AsCustom("TEXT").NotNullable()
            .WithColumn("language").AsCustom("TEXT").NotNullable()
            .WithColumn("file_path").AsCustom("TEXT").NotNullable()
            .WithColumn("match_score").AsInt32().NotNullable()
            .WithColumn("hash_match").AsBoolean().NotNullable()
            .WithColumn("source").AsInt32().NotNullable()
            .WithColumn("search_session_id").AsInt32().Nullable()
            .WithColumn("result").AsInt32().NotNullable()
            .WithColumn("error").AsCustom("TEXT").Nullable()
            .WithColumn("removed_at").AsDateTime().Nullable()
            .WithColumn("replaced_by_download_id").AsInt32().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable();

        // Subtitle-card refresh query for one media item.
        Create.Index("ix_subtitle_downloads_media")
            .OnTable("subtitle_downloads")
            .OnColumn("media_type").Ascending()
            .OnColumn("media_id").Ascending()
            .OnColumn("created_at").Ascending();

        // History screen keyset pagination (created_at > cursor).
        Create.Index("ix_subtitle_downloads_created")
            .OnTable("subtitle_downloads")
            .OnColumn("created_at").Ascending();

        // History filters (source x result).
        Create.Index("ix_subtitle_downloads_source")
            .OnTable("subtitle_downloads")
            .OnColumn("source").Ascending()
            .OnColumn("result").Ascending()
            .OnColumn("created_at").Ascending();

        // Provider-level ban observations (no automatic writer).
        Create.Table("subtitle_search_blacklist")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("provider").AsCustom("TEXT").NotNullable()
            .WithColumn("reason").AsCustom("TEXT").NotNullable()
            .WithColumn("expires_at").AsDateTime().Nullable();

        // Search-request metadata: single source of truth is *arr, Year and
        // ImdbId live on Movie/Show, never Season/Episode (design point 1).
        Alter.Table("movies")
            .AddColumn("year").AsInt32().Nullable();
        Alter.Table("movies")
            .AddColumn("imdb_id").AsCustom("TEXT").Nullable();
        Alter.Table("shows")
            .AddColumn("year").AsInt32().Nullable();
        Alter.Table("shows")
            .AddColumn("imdb_id").AsCustom("TEXT").Nullable();

        // B2 library metadata (quality label from movieFile.quality.quality.
        // name, file size, imdb user rating).
        Alter.Table("movies")
            .AddColumn("quality").AsCustom("TEXT").Nullable();
        Alter.Table("movies")
            .AddColumn("size_bytes").AsCustom("INTEGER").Nullable();
        Alter.Table("movies")
            .AddColumn("imdb_rating").AsCustom("REAL").Nullable();
        Alter.Table("shows")
            .AddColumn("imdb_rating").AsCustom("REAL").Nullable();

        // Feature seeds: the daily 05:00 cron sits an hour after the 04:00
        // sync wave so a round sees same-night syncs; auto score 75 is the
        // single PM-decided threshold for all languages.
        Insert.IntoTable("settings").Row(new { key = EnabledKey, value = "false" });
        Insert.IntoTable("settings").Row(new { key = ScheduleKey, value = "0 5 * * *" });
        Insert.IntoTable("settings").Row(new { key = AutoScoreKey, value = "75" });
        Insert.IntoTable("settings").Row(new { key = ConvertSrtKey, value = "true" });
        Insert.IntoTable("settings").Row(new { key = BatchLimitKey, value = "200" });
        Insert.IntoTable("settings").Row(new { key = AutoTranslateKey, value = "true" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = AutoTranslateKey });
        Delete.FromTable("settings").Row(new { key = BatchLimitKey });
        Delete.FromTable("settings").Row(new { key = ConvertSrtKey });
        Delete.FromTable("settings").Row(new { key = AutoScoreKey });
        Delete.FromTable("settings").Row(new { key = ScheduleKey });
        Delete.FromTable("settings").Row(new { key = EnabledKey });

        Delete.Column("imdb_rating").FromTable("shows");
        Delete.Column("imdb_rating").FromTable("movies");
        Delete.Column("size_bytes").FromTable("movies");
        Delete.Column("quality").FromTable("movies");
        Delete.Column("imdb_id").FromTable("shows");
        Delete.Column("year").FromTable("shows");
        Delete.Column("imdb_id").FromTable("movies");
        Delete.Column("year").FromTable("movies");

        Delete.Table("subtitle_search_blacklist");
        Delete.Index("ix_subtitle_downloads_source").OnTable("subtitle_downloads");
        Delete.Index("ix_subtitle_downloads_created").OnTable("subtitle_downloads");
        Delete.Index("ix_subtitle_downloads_media").OnTable("subtitle_downloads");
        Delete.Table("subtitle_downloads");
        Execute.Sql("DROP INDEX IF EXISTS ix_subtitle_search_state_boost");
        Delete.Index("ux_subtitle_search_state_media").OnTable("subtitle_search_state");
        Delete.Index("ix_subtitle_search_state_dequeue").OnTable("subtitle_search_state");
        Delete.Table("subtitle_search_state");
    }
}
