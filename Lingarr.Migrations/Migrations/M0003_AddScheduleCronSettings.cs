using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Seeds the cron settings rows for CleanupJob and StatisticsJob. Both jobs
/// previously ran on hardcoded fallbacks (Cron.Weekly / Cron.Daily); the
/// schedule screen's job table is now the single edit entry for every cron,
/// so these two built-ins need backing rows like the other three.
///
/// Values are seeded to exactly the previous hardcoded behaviour, so an
/// upgraded instance keeps its schedule with zero drift.
/// </summary>
[Migration(3)]
public class M0003_AddScheduleCronSettings : Migration
{
    private const string CleanupScheduleKey = "cleanup_schedule";
    private const string StatisticsScheduleKey = "statistics_schedule";

    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = CleanupScheduleKey,
            value = "0 0 * * 0"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = StatisticsScheduleKey,
            value = "0 0 * * *"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = CleanupScheduleKey });
        Delete.FromTable("settings").Row(new { key = StatisticsScheduleKey });
    }
}
