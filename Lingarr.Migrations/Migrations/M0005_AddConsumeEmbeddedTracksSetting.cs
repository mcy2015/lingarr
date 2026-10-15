using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// F2b: seeds the settings row for <c>consume_embedded_tracks</c> ("false" by
/// default — the automated translation job keeps ignoring embedded subtitle
/// tracks until the user opts in; manual translation from the media detail
/// page is not gated by this flag).
/// </summary>
[Migration(5)]
public class M0005_AddConsumeEmbeddedTracksSetting : Migration
{
    private const string ConsumeEmbeddedTracksKey = "consume_embedded_tracks";

    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = ConsumeEmbeddedTracksKey,
            value = "false"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = ConsumeEmbeddedTracksKey });
    }
}
