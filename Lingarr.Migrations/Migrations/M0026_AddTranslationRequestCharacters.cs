using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds the per-request source character count recorded when a translation
/// completes. Combined with the per-provider rate settings (A5) this backs the
/// cost rollups: overview KPI, provider health rows and the library tray
/// estimate.
/// </summary>
[Migration(26)]
public class M0026_AddTranslationRequestCharacters : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("characters_translated").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("characters_translated")
                .AsInt32()
                .Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("translation_requests").Column("characters_translated").Exists())
        {
            Delete.Column("characters_translated").FromTable("translation_requests");
        }
    }
}
