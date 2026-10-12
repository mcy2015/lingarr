using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Adds queue-screen runtime fields to translation requests: the translation
/// service that actually processed the request (provider badge), persisted
/// progress (persisted track bars + ETA estimation), the run start time and
/// the Hangfire retry count for failed requests.
/// </summary>
[Migration(25)]
public class M0025_AddTranslationRequestQueueRuntimeFields : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_requests").Column("service_type").Exists())
        {
            Alter.Table("translation_requests").AddColumn("service_type").AsString(100).Nullable();
        }

        if (!Schema.Table("translation_requests").Column("progress").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("progress")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0);
        }

        if (!Schema.Table("translation_requests").Column("started_at").Exists())
        {
            Alter.Table("translation_requests").AddColumn("started_at").AsDateTime().Nullable();
        }

        if (!Schema.Table("translation_requests").Column("retry_count").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("retry_count")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        foreach (var column in new[] { "service_type", "progress", "started_at", "retry_count" })
        {
            if (Schema.Table("translation_requests").Column(column).Exists())
            {
                Delete.Column(column).FromTable("translation_requests");
            }
        }
    }
}
