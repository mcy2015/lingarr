using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// PRD card A (docs/library-enhancements-prd.zh-CN.md) + scale audit P1-7:
/// audio-language awareness and the subtitle sniff-persistence table.
///
/// movies/episodes.audio_languages stores the normalized ISO-code JSON array
/// (["zh","en"]; chi/zho folded onto zh by the shared container-language
/// rules). NULL — never "[]" — means "unknown": an *arr payload without a
/// recognized code leaves the column open for the ffprobe piggyback scan to
/// fill later.
///
/// audio_override is the user's manual mark (true = needs subtitles,
/// false = none needed); NULL = no manual mark. A manual mark outranks every
/// automated source (PRD card A semantics; consumers land in later batches).
///
/// subtitle_language_cache persists the SubtitleService content-sniff results
/// (audit P1-7: the in-memory cache caps at 500 entries, so a 100k library
/// re-read every untagged file head on each scan). Rows are keyed by path and
/// validated against (mtime_ticks, length) — an edit invalidates the entry
/// exactly like the in-memory key. language NULL is a valid "sniffed, still
/// unknown" result and is cached like any other, so unknown files are not
/// re-read either.
/// </summary>
[Migration(7)]
public class M0007_AudioAndLanguageCache : Migration
{
    public override void Up()
    {
        // Card A columns — JSON array of normalized codes; null = unknown.
        Alter.Table("movies")
            .AddColumn("audio_languages").AsCustom("TEXT").Nullable();
        Alter.Table("movies")
            .AddColumn("audio_override").AsBoolean().Nullable();
        Alter.Table("episodes")
            .AddColumn("audio_languages").AsCustom("TEXT").Nullable();
        Alter.Table("episodes")
            .AddColumn("audio_override").AsBoolean().Nullable();

        // needs_translation: maintained by the statistics scan from
        // (audio exemption ∪ coverage gap ∪ manual override). The automated
        // translation job selects candidates on this flag (audit P0-5), so it
        // gets a partial index instead of a full-library walk.
        Alter.Table("movies")
            .AddColumn("needs_translation").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("episodes")
            .AddColumn("needs_translation").AsBoolean().NotNullable().WithDefaultValue(false);
        // Persisted exemption flag (QA S4 P1-1): default 0 means nothing is
        // shown as native before the first statistics scan populates it, and
        // the native filter reads exactly this flag (single source).
        Alter.Table("movies")
            .AddColumn("no_subtitle_needed").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("episodes")
            .AddColumn("no_subtitle_needed").AsBoolean().NotNullable().WithDefaultValue(false);
        // SQLite partial indexes are not expressible in FluentMigrator's
        // fluent surface; raw DDL matches what EF's migrations would emit.
        Execute.Sql("CREATE INDEX ix_movies_needs_translation ON movies (needs_translation) WHERE needs_translation = 1");
        Execute.Sql("CREATE INDEX ix_episodes_needs_translation ON episodes (needs_translation) WHERE needs_translation = 1");

        // Audit P1-7: persisted sniff results, path-keyed.
        Create.Table("subtitle_language_cache")
            .WithColumn("path").AsCustom("TEXT").NotNullable().PrimaryKey()
            .WithColumn("mtime_ticks").AsInt64().NotNullable()
            .WithColumn("length").AsInt64().NotNullable()
            .WithColumn("language").AsCustom("TEXT").Nullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("subtitle_language_cache");
        Execute.Sql("DROP INDEX IF EXISTS ix_episodes_needs_translation");
        Execute.Sql("DROP INDEX IF EXISTS ix_movies_needs_translation");
        Delete.Column("no_subtitle_needed").FromTable("episodes");
        Delete.Column("no_subtitle_needed").FromTable("movies");
        Delete.Column("needs_translation").FromTable("episodes");
        Delete.Column("needs_translation").FromTable("movies");
        Delete.Column("audio_override").FromTable("episodes");
        Delete.Column("audio_languages").FromTable("episodes");
        Delete.Column("audio_override").FromTable("movies");
        Delete.Column("audio_languages").FromTable("movies");
    }
}
