using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Media-file detail batch (detail page parity with Sonarr/Radarr's file
/// info): the *arr movieFile/episodeFile blocks already flow through the
/// sync deserialization — these columns persist the parts the file card
/// and the episode table render.
///
/// <para>
/// movies + episodes: <c>video_codec</c>/<c>audio_codec</c> (mediaInfo
/// strings, stored as served), <c>audio_channels</c> (REAL — 5.1),
/// <c>resolution</c> (pixel string "1920x1080") and
/// <c>runtime_minutes</c> (INTEGER — parsed server-side from runTime
/// "1:57:00" / "43:12"). Episodes additionally gain <c>quality</c> and
/// <c>size_bytes</c>, which movies already carry (episode rows had no
/// per-file columns — a series was judged to have no single file; the
/// episode table now shows them per row, Sonarr-style).
/// </para>
/// <para>
/// movies + shows: <c>overview</c> — the TMDb plot summary served by
/// *arr's movie/series payloads, rendered in the detail page's poster +
/// 简介 block. Payload-authoritative on re-sync like the other B2
/// metadata (no lower-trust local source to protect).
/// </para>
/// </summary>
[Migration(13)]
public class M0013_MediaFileInfo : Migration
{
    public override void Up()
    {
        Alter.Table("movies")
            .AddColumn("overview").AsCustom("TEXT").Nullable()
            .AddColumn("video_codec").AsCustom("TEXT").Nullable()
            .AddColumn("audio_codec").AsCustom("TEXT").Nullable()
            .AddColumn("audio_channels").AsDouble().Nullable()
            .AddColumn("resolution").AsCustom("TEXT").Nullable()
            .AddColumn("runtime_minutes").AsInt32().Nullable();
        Alter.Table("shows")
            .AddColumn("overview").AsCustom("TEXT").Nullable();
        Alter.Table("episodes")
            .AddColumn("quality").AsCustom("TEXT").Nullable()
            .AddColumn("size_bytes").AsInt64().Nullable()
            .AddColumn("video_codec").AsCustom("TEXT").Nullable()
            .AddColumn("audio_codec").AsCustom("TEXT").Nullable()
            .AddColumn("audio_channels").AsDouble().Nullable()
            .AddColumn("resolution").AsCustom("TEXT").Nullable()
            .AddColumn("runtime_minutes").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Column("overview").FromTable("movies");
        Delete.Column("video_codec").FromTable("movies");
        Delete.Column("audio_codec").FromTable("movies");
        Delete.Column("audio_channels").FromTable("movies");
        Delete.Column("resolution").FromTable("movies");
        Delete.Column("runtime_minutes").FromTable("movies");
        Delete.Column("overview").FromTable("shows");
        Delete.Column("quality").FromTable("episodes");
        Delete.Column("size_bytes").FromTable("episodes");
        Delete.Column("video_codec").FromTable("episodes");
        Delete.Column("audio_codec").FromTable("episodes");
        Delete.Column("audio_channels").FromTable("episodes");
        Delete.Column("resolution").FromTable("episodes");
        Delete.Column("runtime_minutes").FromTable("episodes");
    }
}
