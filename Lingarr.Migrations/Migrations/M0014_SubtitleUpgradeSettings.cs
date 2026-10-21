using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Subtitle upgrade settings seeds (B02, the bazarr-upgrade port): four
/// rows, no schema. The upgrade round reads <c>subtitle_downloads</c> and
/// lands through the existing download pipeline, so the only new facts are
/// settings:
///
/// <list type="bullet">
/// <item><c>subtitle_upgrade_enabled</c> — "false": re-searching the library
/// and REPLACING stored subtitle files must never run implicitly (the
/// B01 converting-stored-text philosophy, applied to replacing it).</item>
/// <item><c>subtitle_upgrade_schedule</c> — "0 4 * * *": inside the 04:00
/// maintenance wave (M0008 seeded the search batch at 05:00, an hour behind
/// the sync wave; upgrades re-offer what already landed, so the sync order
/// does not matter to them).</item>
/// <item><c>subtitle_upgrade_score_margin</c> — "3": the minimum score gain
/// an upgrade must beat, the anti-loop guard (same score never replaces).</item>
/// <item><c>subtitle_upgrade_window_days</c> — "14": only files landed inside
/// the window are re-offered, which bounds every round's candidate set.</item>
/// </list>
///
/// <para>M0008 precedent: a feature's seeded settings ride a migration —
/// FluentMigrator applies it to fresh and existing databases alike, so no
/// StartupService seed is needed (that path exists for rows introduced
/// without a migration).</para>
/// </summary>
[Migration(14)]
public class M0014_SubtitleUpgradeSettings : Migration
{
    private const string EnabledKey = "subtitle_upgrade_enabled";
    private const string ScheduleKey = "subtitle_upgrade_schedule";
    private const string MarginKey = "subtitle_upgrade_score_margin";
    private const string WindowKey = "subtitle_upgrade_window_days";

    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = EnabledKey, value = "false" });
        Insert.IntoTable("settings").Row(new { key = ScheduleKey, value = "0 4 * * *" });
        Insert.IntoTable("settings").Row(new { key = MarginKey, value = "3" });
        Insert.IntoTable("settings").Row(new { key = WindowKey, value = "14" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = WindowKey });
        Delete.FromTable("settings").Row(new { key = MarginKey });
        Delete.FromTable("settings").Row(new { key = ScheduleKey });
        Delete.FromTable("settings").Row(new { key = EnabledKey });
    }
}
