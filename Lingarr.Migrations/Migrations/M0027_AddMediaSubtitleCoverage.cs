using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds per-media subtitle coverage (JSON: language -> episode count + bytes)
/// to shows and movies, maintained by the statistics scan. The media library
/// matrix and the tray cost estimate read it without re-walking the library
/// filesystem on every page view.
/// </summary>
[Migration(27)]
public class M0027_AddMediaSubtitleCoverage : Migration
{
    public override void Up()
    {
        foreach (var table in new[] { "shows", "movies" })
        {
            if (!Schema.Table(table).Column("subtitle_coverage_json").Exists())
            {
                Alter.Table(table)
                    .AddColumn("subtitle_coverage_json")
                    .AsString(int.MaxValue)
                    .Nullable();
            }
        }
    }

    public override void Down()
    {
        foreach (var table in new[] { "shows", "movies" })
        {
            if (Schema.Table(table).Column("subtitle_coverage_json").Exists())
            {
                Delete.Column("subtitle_coverage_json").FromTable(table);
            }
        }
    }
}
