using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Per-media target-language overrides (B08, the bazarr language-profile
/// subset): the media-level code set the subtitle-need rules resolve against
/// instead of the global <c>target_languages</c> setting.
///
/// <list type="bullet">
/// <item><c>movies.target_languages_override</c> / <c>shows.target_languages_override</c>
/// — comma-separated language codes ("zh,en") as TEXT, nullable.</item>
/// <item>NULL — the default — means "follow the global list": media without
/// an override keeps exactly its pre-B08 behavior (regression gate).</item>
/// <item>Empty string is the explicit「无目标」state: the media wants no
/// subtitles, the need rules judge "nothing missing" and the gap queue skips
/// it. Distinct from the no_subtitle_needed exemption flag (audio-based,
/// separate semantics) — flipping the override back restores normal
/// behavior with one write.</item>
/// <item>Episodes add no column: they inherit their show's override through
/// the season, exactly like the show-level need-flag recomputation.</item>
/// </list>
///
/// <para>No settings seeds — the global list stays the single global
/// configuration; the override columns are pure per-media data.</para>
/// </summary>
[Migration(15)]
public class M0015_PerMediaTargetLanguages : Migration
{
    public override void Up()
    {
        Alter.Table("movies")
            .AddColumn("target_languages_override").AsCustom("TEXT").Nullable();
        Alter.Table("shows")
            .AddColumn("target_languages_override").AsCustom("TEXT").Nullable();
    }

    public override void Down()
    {
        Delete.Column("target_languages_override").FromTable("movies");
        Delete.Column("target_languages_override").FromTable("shows");
    }
}
