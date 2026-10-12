using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds the content hash to translation requests so the import workbench can
/// detect re-imported subtitles regardless of file name (SHA-256 of normalized
/// content). Null for library-originated requests, which do not participate
/// in import dedup.
/// </summary>
[Migration(24)]
public class M0024_AddTranslationRequestContentHash : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("content_hash").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("content_hash")
                .AsString(64)
                .Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("translation_requests").Column("content_hash").Exists())
        {
            Delete.Column("content_hash").FromTable("translation_requests");
        }
    }
}
