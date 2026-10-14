using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Single baseline migration: the squashed end state of the original 29-migration
/// chain (M0001_InitialCreate .. M0029_AddTranslationRequestEventDetails) for SQLite.
/// Fresh databases run this one migration and land on the exact schema and seed
/// data the full historical chain produced — verified column-by-column against a
/// database built by the old chain (PRAGMA table_info / foreign_key_list /
/// index_list and sqlite_master diff).
///
/// Version note: this migration deliberately reuses version 1. Databases that
/// already completed the old chain (version_info at 29) therefore see no pending
/// migration and keep working, since their schema is identical to this baseline.
/// Partially migrated (pre-29) databases cannot be upgraded in place — rebuild
/// them from scratch (the accepted trade-off of the squash).
/// </summary>
[Migration(1)]
public class M0001_Baseline : Migration
{
    public override void Up()
    {
        // Settings
        Create.Table("settings")
            .WithColumn("key").AsString(255).PrimaryKey()
            .WithColumn("value").AsCustom("TEXT").NotNullable()
            .WithColumn("provider").AsString(255).Nullable();

        // Movies
        Create.Table("movies")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("radarr_id").AsInt32().NotNullable()
            .WithColumn("title").AsCustom("TEXT").NotNullable()
            .WithColumn("file_name").AsCustom("TEXT").Nullable()
            .WithColumn("path").AsCustom("TEXT").Nullable()
            .WithColumn("media_hash").AsCustom("TEXT").Nullable()
            .WithColumn("date_added").AsDateTime().Nullable()
            .WithColumn("translation_age_threshold").AsInt32().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("include_in_translation").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("subtitle_coverage_json").AsString(int.MaxValue).Nullable();

        // Shows
        Create.Table("shows")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("sonarr_id").AsInt32().NotNullable()
            .WithColumn("title").AsCustom("TEXT").NotNullable()
            .WithColumn("path").AsCustom("TEXT").NotNullable()
            .WithColumn("date_added").AsDateTime().Nullable()
            .WithColumn("translation_age_threshold").AsInt32().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("include_in_translation").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("subtitle_coverage_json").AsString(int.MaxValue).Nullable();

        // Seasons
        Create.Table("seasons")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("season_number").AsInt32().NotNullable()
            .WithColumn("path").AsCustom("TEXT").Nullable()
            .WithColumn("show_id").AsInt32()
                .NotNullable()
                .ForeignKey("fk_seasons_shows_show_id", "shows", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade)
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("include_in_translation").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Index("ix_seasons_show_id").OnTable("seasons").OnColumn("show_id");

        // Episodes
        Create.Table("episodes")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("sonarr_id").AsInt32().NotNullable()
            .WithColumn("episode_number").AsInt32().NotNullable()
            .WithColumn("title").AsCustom("TEXT").NotNullable()
            .WithColumn("file_name").AsCustom("TEXT").Nullable()
            .WithColumn("path").AsCustom("TEXT").Nullable()
            .WithColumn("media_hash").AsCustom("TEXT").Nullable()
            .WithColumn("season_id").AsInt32()
                .NotNullable()
                .ForeignKey("fk_episodes_seasons_season_id", "seasons", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade)
            .WithColumn("date_added").AsDateTime().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("include_in_translation").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Index("ix_episodes_season_id").OnTable("episodes").OnColumn("season_id");

        // Images
        Create.Table("images")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("type").AsCustom("TEXT").NotNullable()
            .WithColumn("path").AsCustom("TEXT").NotNullable()
            .WithColumn("show_id")
                .AsInt32()
                .Nullable()
                .ForeignKey("fk_images_shows_show_id", "shows", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade)
            .WithColumn("movie_id")
                .AsInt32()
                .Nullable()
                .ForeignKey("fk_images_movies_movie_id", "movies", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade);

        Create.Index("ix_images_show_id").OnTable("images").OnColumn("show_id");
        Create.Index("ix_images_movie_id").OnTable("images").OnColumn("movie_id");

        // Path mappings
        Create.Table("path_mappings")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("source_path").AsCustom("TEXT").NotNullable()
            .WithColumn("destination_path").AsCustom("TEXT").NotNullable()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        // Translation requests
        Create.Table("translation_requests")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("job_id").AsCustom("TEXT").Nullable()
            .WithColumn("media_id").AsInt32().Nullable()
            .WithColumn("title").AsCustom("TEXT").NotNullable()
            .WithColumn("source_language").AsCustom("TEXT").NotNullable()
            .WithColumn("target_language").AsCustom("TEXT").NotNullable()
            .WithColumn("subtitle_to_translate").AsCustom("TEXT").Nullable()
            .WithColumn("translated_subtitle").AsCustom("TEXT").Nullable()
            .WithColumn("media_type").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("completed_at").AsDateTime().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("error_message").AsCustom("TEXT").Nullable()
            .WithColumn("stack_trace").AsCustom("TEXT").Nullable()
            .WithColumn("job_type").AsInt32().Nullable()
            .WithColumn("is_bilingual").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("source").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("content_hash").AsString(64).Nullable()
            .WithColumn("service_type").AsString(100).Nullable()
            .WithColumn("progress").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("started_at").AsDateTime().Nullable()
            .WithColumn("retry_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("characters_translated").AsInt32().Nullable();

        // Translation request events
        Create.Table("translation_request_events")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("translation_request_id").AsInt32()
                .NotNullable()
                .ForeignKey("fk_translation_request_events_translation_requests_id", "translation_requests", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade)
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("message").AsCustom("TEXT").Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("event_type").AsInt32().Nullable()
            .WithColumn("provider").AsCustom("TEXT").Nullable();

        Create.Index("ix_translation_request_events_translation_request_id")
            .OnTable("translation_request_events")
            .OnColumn("translation_request_id");

        // Translation request lines
        Create.Table("translation_request_lines")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("translation_request_id").AsInt32()
                .NotNullable()
                .ForeignKey("fk_translation_request_lines_translation_requests_id", "translation_requests", "id")
                .OnDeleteOrUpdate(System.Data.Rule.Cascade)
            .WithColumn("position").AsInt32().NotNullable()
            .WithColumn("source").AsCustom("TEXT").NotNullable()
            .WithColumn("target").AsCustom("TEXT").NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("service").AsString(50).Nullable()
            .WithColumn("matched_source").AsString(50).Nullable()
            .WithColumn("matched_target").AsString(50).Nullable()
            .WithColumn("tier").AsInt32().Nullable();

        Create.Index("ix_translation_request_lines_translation_request_id")
            .OnTable("translation_request_lines")
            .OnColumn("translation_request_id");

        // Glossary
        Create.Table("translation_glossaries")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("source_term").AsString(500).NotNullable()
            .WithColumn("target_term").AsString(500).NotNullable()
            .WithColumn("note").AsString(1000).Nullable()
            .WithColumn("is_locked").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        Create.Index("ix_glossary_source_term").OnTable("translation_glossaries")
            .OnColumn("source_term");

        // Statistics
        Create.Table("statistics")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("total_lines_translated").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("total_files_translated").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("total_characters_translated").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("total_movies").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("total_episodes").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("total_subtitles").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("translations_by_media_type_json").AsCustom("TEXT").NotNullable()
            .WithColumn("translations_by_service_json").AsCustom("TEXT").NotNullable()
            .WithColumn("subtitles_by_language_json").AsCustom("TEXT").NotNullable()
            .WithColumn("translations_by_model_json").AsCustom("TEXT").NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        // Daily statistics
        Create.Table("daily_statistics")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("date").AsDateTime().NotNullable()
            .WithColumn("translation_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        // Users
        Create.Table("users")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("username").AsCustom("TEXT").NotNullable()
            .WithColumn("password_hash").AsCustom("TEXT").NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("last_login_at").AsDateTime().Nullable();

        SeedSettings();
    }

    public override void Down()
    {
        Delete.Table("translation_request_lines");
        Delete.Table("translation_request_events");
        Delete.Table("translation_glossaries");
        Delete.Table("translation_requests");
        Delete.Table("daily_statistics");
        Delete.Table("statistics");
        Delete.Table("users");
        Delete.Table("path_mappings");
        Delete.Table("images");
        Delete.Table("episodes");
        Delete.Table("seasons");
        Delete.Table("shows");
        Delete.Table("movies");
        Delete.Table("settings");
    }

    /// <summary>
    /// The net settings seed of the full historical chain: every row the old
    /// migrations inserted, minus the ones later migrations removed
    /// (custom_ai_parameters, ai_context_prompt, ai_context_prompt_enabled,
    /// telemetry_last_reported_*). Values transcribed from the end state of the
    /// old chain.
    /// </summary>
    private void SeedSettings()
    {
        Insert.IntoTable("settings").Row(new { key = "add_translator_info", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "ai_context_after", value = "2" });
        Insert.IntoTable("settings").Row(new { key = "ai_context_before", value = "2" });
        Insert.IntoTable("settings").Row(new { key = "ai_context_use_translated", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "ai_prompt", value = "Translate from {sourceLanguage} to {targetLanguage}, preserving the tone and meaning without censoring the content. Adjust punctuation as needed to make the translation sound natural. Provide only the translated text as output, with no additional comments." });
        Insert.IntoTable("settings").Row(new { key = "ai_user_prompt", value = "{lineToTranslate}" });
        Insert.IntoTable("settings").Row(new { key = "anthropic_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "anthropic_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "anthropic_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "anthropic_version", value = "" });
        Insert.IntoTable("settings").Row(new { key = "api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "api_key_enabled", value = "" });
        // Secure default: fresh installs boot with authentication enabled
        // (login page, seeded admin/admin). Existing databases keep their
        // stored value; this row is only inserted on first migration.
        Insert.IntoTable("settings").Row(new { key = "auth_enabled", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "automation_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "bilingual_subtitle_output", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "deepl_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "deepseek_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "deepseek_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "deepseek_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "fix_overlapping_subtitles", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "gemini_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "gemini_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "gemini_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "ignore_captions", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "language_code_format", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "libretranslate_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "libretranslate_url", value = "" });
        Insert.IntoTable("settings").Row(new { key = "local_ai_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "local_ai_chat_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "local_ai_endpoint", value = "" });
        Insert.IntoTable("settings").Row(new { key = "local_ai_generate_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "local_ai_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "locale", value = "en" });
        Insert.IntoTable("settings").Row(new { key = "max_batch_size", value = "0" });
        Insert.IntoTable("settings").Row(new { key = "max_retries", value = "5" });
        Insert.IntoTable("settings").Row(new { key = "max_translations_per_run", value = "10" });
        Insert.IntoTable("settings").Row(new { key = "mistral_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "mistral_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "mistral_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "movie_age_threshold", value = "0" });
        Insert.IntoTable("settings").Row(new { key = "movie_schedule", value = "0 4 * * *" });
        Insert.IntoTable("settings").Row(new { key = "navigate_to_details_on_request", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "onboarding_completed", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "openai_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "openai_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "openai_request_template", value = "" });
        Insert.IntoTable("settings").Row(new { key = "preserve_line_breaks", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "proofread_prompt", value = "You are proofreading a subtitle translated from {sourceLanguage} to {targetLanguage}. Compare the translation against the source and correct mistranslations, wrong names, grammar and punctuation, preserving the tone and meaning without censoring the content. Keep the length close to the original so it still fits on screen. If the translation is already correct, repeat it unchanged. Provide only the corrected translation as output, with no additional comments." });
        Insert.IntoTable("settings").Row(new { key = "proofread_user_prompt", value = "[SOURCE] {sourceLine}\n[TRANSLATION] {translatedLine}" });
        Insert.IntoTable("settings").Row(new { key = "radarr_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "radarr_default_include", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "radarr_settings_completed", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "radarr_url", value = "" });
        Insert.IntoTable("settings").Row(new { key = "remove_language_tag", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "request_timeout", value = "5" });
        Insert.IntoTable("settings").Row(new { key = "retry_delay", value = "1" });
        Insert.IntoTable("settings").Row(new { key = "retry_delay_multiplier", value = "2" });
        Insert.IntoTable("settings").Row(new { key = "service_type", value = "libretranslate" });
        Insert.IntoTable("settings").Row(new { key = "show_age_threshold", value = "0" });
        Insert.IntoTable("settings").Row(new { key = "show_schedule", value = "0 4 * * *" });
        Insert.IntoTable("settings").Row(new { key = "sonarr_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "sonarr_default_include", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "sonarr_settings_completed", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "sonarr_url", value = "" });
        Insert.IntoTable("settings").Row(new { key = "source_languages", value = "[]" });
        Insert.IntoTable("settings").Row(new { key = "strip_subtitle_formatting", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_tag", value = "lingarr" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_maxdurationsecs", value = "10" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_maxfilesizebytes", value = "1048576" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_maxsubtitlelength", value = "500" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_mindurationms", value = "500" });
        Insert.IntoTable("settings").Row(new { key = "subtitle_validation_minsubtitlelength", value = "2" });
        Insert.IntoTable("settings").Row(new { key = "target_languages", value = "[]" });
        Insert.IntoTable("settings").Row(new { key = "telemetry_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "telemetry_installation_id", value = "" });
        Insert.IntoTable("settings").Row(new { key = "telemetry_last_submission", value = "" });
        Insert.IntoTable("settings").Row(new { key = "theme", value = "lingarr" });
        Insert.IntoTable("settings").Row(new { key = "translation_cycle", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "translation_schedule", value = "0 4 * * *" });
        Insert.IntoTable("settings").Row(new { key = "use_batch_translation", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "use_subtitle_tagging", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "xai_api_key", value = "" });
        Insert.IntoTable("settings").Row(new { key = "xai_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "xai_request_template", value = "" });
    }
}
