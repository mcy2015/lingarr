using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds the source discriminator to translation requests so manually imported
/// subtitles (import workbench) can be told apart from *arr library media.
/// Imported requests keep a null media id and ride the same pipeline; the value
/// mirrors <see cref="Lingarr.Core.Enum.TranslationRequestSource"/> as an int
/// (0 = library, 1 = import), matching EF's default enum mapping.
/// </summary>
[Migration(23)]
public class M0023_AddTranslationRequestSource : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("source").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("source")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0); // TranslationRequestSource.Library
        }
    }

    public override void Down()
    {
        if (Schema.Table("translation_requests").Column("source").Exists())
        {
            Delete.Column("source").FromTable("translation_requests");
        }
    }
}
