using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// F2a embedded subtitle tracks: one table for the ffprobe scan results plus
/// the cron settings row for the new EmbeddedTrackScanJob. The seeded cron
/// (daily 04:30) intentionally sits half an hour after the 04:00 sync wave so
/// fresh-library scans probe files that synced the same night.
/// </summary>
[Migration(4)]
public class M0004_AddEmbeddedSubtitleTracks : Migration
{
    private const string EmbeddedTrackScheduleKey = "embedded_track_schedule";

    public override void Up()
    {
        Create.Table("media_subtitle_tracks")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("media_id").AsInt32().NotNullable()
            .WithColumn("stream_index").AsInt32().NotNullable()
            .WithColumn("language").AsCustom("TEXT").Nullable()
            .WithColumn("raw_language").AsCustom("TEXT").Nullable()
            .WithColumn("title").AsCustom("TEXT").Nullable()
            .WithColumn("codec").AsString(50).NotNullable()
            .WithColumn("is_text_based").AsBoolean().NotNullable()
            .WithColumn("file_path").AsCustom("TEXT").NotNullable()
            .WithColumn("media_hash").AsCustom("TEXT").Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        Create.Index("ix_media_subtitle_tracks_media_type_media_id")
            .OnTable("media_subtitle_tracks")
            .OnColumn("media_type").Ascending()
            .OnColumn("media_id").Ascending();

        Insert.IntoTable("settings").Row(new
        {
            key = EmbeddedTrackScheduleKey,
            value = "30 4 * * *"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = EmbeddedTrackScheduleKey });
        Delete.Table("media_subtitle_tracks");
    }
}
