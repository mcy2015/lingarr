using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Audio-language confirmation batch (walk-through options B/E): a
/// TMDB-derived suggestion column and a manual-audio-languages path.
///
/// <para>
/// <c>suggested_language</c> (movies + shows): the work's original language
/// from the *arr payload (Radarr movie.originalLanguage, Sonarr series
/// originalLanguage — TMDb spoken language). It is a GUESS for files whose
/// container carries no language tag (dubs betray it), so it never lands in
/// <c>audio_languages</c> — that column stays null for the ffprobe piggyback
/// to fill with real container truth. The UI shows it as a muted「推测」hint
/// next to an unknown audio row and in the library audio column. Shows carry
/// it at the series level (episodes aggregate the series guess).
/// </para>
/// <para>
/// <c>audio_languages_manual</c> (movies + episodes): provenance flag flipped
/// by the manual language editor (option E) and the remote identification
/// action (option C) — a stored value survives a later sync whose payload
/// carries nothing, and the guard belongs on the row the sync actually
/// writes (episodes, not the aggregated show), mirroring the audio_override
/// precedent.
/// </para>
/// </summary>
[Migration(12)]
public class M0012_SuggestedLanguageAndManualAudio : Migration
{
    public override void Up()
    {
        Alter.Table("movies")
            .AddColumn("suggested_language").AsCustom("TEXT").Nullable()
            .AddColumn("audio_languages_manual").AsBoolean().Nullable();
        Alter.Table("shows")
            .AddColumn("suggested_language").AsCustom("TEXT").Nullable();
        Alter.Table("episodes")
            .AddColumn("audio_languages_manual").AsBoolean().Nullable();
    }

    public override void Down()
    {
        Delete.Column("suggested_language").FromTable("movies");
        Delete.Column("audio_languages_manual").FromTable("movies");
        Delete.Column("suggested_language").FromTable("shows");
        Delete.Column("audio_languages_manual").FromTable("episodes");
    }
}
