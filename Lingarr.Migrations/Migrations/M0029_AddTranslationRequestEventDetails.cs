using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// P1-2 fallback visibility: event kind and provider on the request event
/// timeline. Both nullable — legacy rows stay pure status transitions.
/// </summary>
[Migration(29)]
public class M0029_AddTranslationRequestEventDetails : Migration
{
    public override void Up()
    {
        Alter.Table("translation_request_events")
            .AddColumn("event_type").AsInt32().Nullable()
            .AddColumn("provider").AsCustom("TEXT").Nullable();
    }

    public override void Down()
    {
        Delete.Column("event_type").FromTable("translation_request_events");
        Delete.Column("provider").FromTable("translation_request_events");
    }
}
