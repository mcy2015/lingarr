using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Subtitle-search schema cleanup (P2 data batch):
///
/// - Drops the never-used replacement-chain columns
/// (<c>removed_at</c>, <c>replaced_by_download_id</c>) from
/// <c>subtitle_downloads</c>. The "overwrite/supersede" semantics were ruled
/// out — a re-download simply lands a new file and a new history row — so the
/// two nullable columns stayed null forever while suggesting a chain that no
/// writer maintains. Dead schema misleads; it goes.
/// - Adds <c>external_id</c> to <c>subtitle_search_blacklist</c>. M0008
/// created the table for hand-written provider-level observations only; the
/// download pipeline now writes automatic per-candidate bans keyed on
/// (provider, external_id) — the locked read/write contract for the search
/// filter side. Existing operator rows backfill to <c>''</c>, which never
/// matches a real candidate id under the exact-match rule.
/// - Adds the (provider, external_id) lookup index that the filter-side
/// exact match runs on.
/// </summary>
[Migration(10)]
public class M0010_CleanSubtitleSearchSchema : Migration
{
    public override void Up()
    {
        // Dead columns: created by M0008, never written by any code path.
        Delete.Column("removed_at").FromTable("subtitle_downloads");
        Delete.Column("replaced_by_download_id").FromTable("subtitle_downloads");

        // Blacklist write side (locked contract): rows are keyed on
        // (provider, external_id); the empty default keeps hand-written
        // provider-level rows insertable without naming an id.
        Alter.Table("subtitle_search_blacklist")
            .AddColumn("external_id").AsCustom("TEXT").NotNullable().WithDefaultValue(string.Empty);

        Create.Index("ix_subtitle_search_blacklist_provider")
            .OnTable("subtitle_search_blacklist")
            .OnColumn("provider").Ascending()
            .OnColumn("external_id").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_subtitle_search_blacklist_provider").OnTable("subtitle_search_blacklist");
        Delete.Column("external_id").FromTable("subtitle_search_blacklist");

        // Full restore of the M0008 shape (both columns nullable, no data to
        // rebuild — they were never written).
        Alter.Table("subtitle_downloads")
            .AddColumn("removed_at").AsDateTime().Nullable();
        Alter.Table("subtitle_downloads")
            .AddColumn("replaced_by_download_id").AsInt32().Nullable();
    }
}
