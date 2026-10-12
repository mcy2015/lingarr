using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds the is_bilingual flag to translation requests so the output form
/// (monolingual vs bilingual) can be tracked and converted after the fact,
/// and seeds the bilingual_subtitle_output setting default.
/// </summary>
[Migration(22)]
public class M0022_AddTranslationRequestIsBilingual : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("is_bilingual").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("is_bilingual")
                .AsBoolean()
                .NotNullable()
                .WithDefaultValue(false);
        }

        Insert.IntoTable("settings").Row(new { key = "bilingual_subtitle_output", value = "false" });
    }

    public override void Down()
    {
        if (Schema.Table("translation_requests").Column("is_bilingual").Exists())
        {
            Delete.Column("is_bilingual").FromTable("translation_requests");
        }

        Delete.FromTable("settings").Row(new { key = "bilingual_subtitle_output" });
    }
}
