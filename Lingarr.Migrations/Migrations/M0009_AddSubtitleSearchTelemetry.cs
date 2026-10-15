using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Subtitle search telemetry (tech design ④, M1 batch):
///
/// - <c>subtitle_search_sessions</c> logs one row per (media, provider,
///   language) query — a zero-result query also lands a row, which is what
///   makes the per-source hit-rate KPI honest.
/// - <c>provider_quota_usage</c> is the daily counter/waterline per source,
///   upserted transactionally after every Search/Download (⑩); quota_limit
///   carries the source constant (e.g. OpenSubtitles' 40/day) for display
///   only, quota_remaining is null for sources that do not report one.
/// - <c>coverage_daily</c> is the per-day, per-type, per-language coverage
///   snapshot, maintained by incremental upserts, never a full rescan.
/// - movies/episodes gain the coverage_checked_at watermark column. It is
///   created now but only written from the M7 statistics watermark batch —
///   "create the column first, use it later" so that batch needs no DDL.
///
/// The two counter tables are created with raw DDL (M0007 precedent for
/// generator gaps): FluentMigrator's SQLite generator emits composite
/// primary keys inline per column, which SQLite rejects, and the exact
/// column types matter for the PK upsert statements.
/// </summary>
[Migration(9)]
public class M0009_AddSubtitleSearchTelemetry : Migration
{
    public override void Up()
    {
        // One row per search query, including the no-result ones.
        Create.Table("subtitle_search_sessions")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("media_id").AsInt32().NotNullable()
            .WithColumn("provider").AsCustom("TEXT").NotNullable()
            .WithColumn("query_language").AsCustom("TEXT").NotNullable()
            .WithColumn("result_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("hit_target").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTime().NotNullable();

        Create.Index("ix_subtitle_search_sessions_media")
            .OnTable("subtitle_search_sessions")
            .OnColumn("media_type").Ascending()
            .OnColumn("media_id").Ascending()
            .OnColumn("created_at").Ascending();
        Create.Index("ix_subtitle_search_sessions_created")
            .OnTable("subtitle_search_sessions")
            .OnColumn("created_at").Ascending();

        // Daily per-source counters; the composite PK is the upsert target
        // (INSERT ... ON CONFLICT(date, provider) DO UPDATE ...+1). The date
        // is stored as TEXT (yyyy-MM-dd, UTC day).
        Execute.Sql(
            "CREATE TABLE provider_quota_usage (" +
            "date TEXT NOT NULL, " +
            "provider TEXT NOT NULL, " +
            "searches INTEGER NOT NULL DEFAULT 0, " +
            "downloads INTEGER NOT NULL DEFAULT 0, " +
            "quota_limit INTEGER NOT NULL DEFAULT 0, " +
            "quota_remaining INTEGER, " +
            "PRIMARY KEY (date, provider))");

        // Daily coverage snapshot; composite PK upsert, never a full rewrite.
        Execute.Sql(
            "CREATE TABLE coverage_daily (" +
            "date TEXT NOT NULL, " +
            "media_type INTEGER NOT NULL, " +
            "language TEXT NOT NULL, " +
            "covered INTEGER NOT NULL DEFAULT 0, " +
            "total INTEGER NOT NULL DEFAULT 0, " +
            "PRIMARY KEY (date, media_type, language))");

        // Coverage watermark (M7 statistics batch consumes it; column first).
        Alter.Table("movies")
            .AddColumn("coverage_checked_at").AsDateTime().Nullable();
        Alter.Table("episodes")
            .AddColumn("coverage_checked_at").AsDateTime().Nullable();
    }

    public override void Down()
    {
        Delete.Column("coverage_checked_at").FromTable("episodes");
        Delete.Column("coverage_checked_at").FromTable("movies");
        Execute.Sql("DROP TABLE IF EXISTS coverage_daily");
        Execute.Sql("DROP TABLE IF EXISTS provider_quota_usage");
        Delete.Index("ix_subtitle_search_sessions_created").OnTable("subtitle_search_sessions");
        Delete.Index("ix_subtitle_search_sessions_media").OnTable("subtitle_search_sessions");
        Delete.Table("subtitle_search_sessions");
    }
}
