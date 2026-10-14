namespace Lingarr.Core.Configuration;

public static class SettingKeys
{
    public static class Integration
    {
        public const string RadarrUrl = "radarr_url";
        public const string RadarrApiKey = "radarr_api_key";
        public const string RadarrDefaultInclude = "radarr_default_include";
        public const string SonarrUrl = "sonarr_url";
        public const string SonarrApiKey = "sonarr_api_key";
        public const string SonarrDefaultInclude = "sonarr_default_include";
        public const string RadarrSettingsCompleted = "radarr_settings_completed";
        public const string SonarrSettingsCompleted = "sonarr_settings_completed";
    }

    public static class Cost
    {
        /// <summary>
        /// JSON map of provider -> USD per 1M source characters, e.g.
        /// {"openai": 2.5, "deepl": 25.0}. Providers without an entry are
        /// priced as unknown (null) rather than free.
        /// </summary>
        public const string ProviderRates = "provider_rates";
    }

    public static class Translation
    {
        public const string ServiceType = "service_type";
        public const string DefaultServiceType = "libretranslate";

        public static class OpenAi
        {
            public const string Model = "openai_model";
            public const string ApiKey = "openai_api_key";
            public const string RequestTemplate = "openai_request_template";
        }

        public static class Anthropic
        {
            public const string Model = "anthropic_model";
            public const string ApiKey = "anthropic_api_key";
            public const string Version = "anthropic_version";
            public const string RequestTemplate = "anthropic_request_template";
        }

        public static class LocalAi
        {
            public const string Model = "local_ai_model";
            public const string Endpoint = "local_ai_endpoint";
            public const string ApiKey = "local_ai_api_key";
            public const string ChatRequestTemplate = "local_ai_chat_request_template";
            public const string GenerateRequestTemplate = "local_ai_generate_request_template";
        }

        public static class DeepL
        {
            public const string DeeplApiKey = "deepl_api_key";
        }

        public static class Gemini
        {
            public const string Model = "gemini_model";
            public const string ApiKey = "gemini_api_key";
            public const string RequestTemplate = "gemini_request_template";
        }

        public static class DeepSeek
        {
            public const string Model = "deepseek_model";
            public const string ApiKey = "deepseek_api_key";
            public const string RequestTemplate = "deepseek_request_template";
        }

        public static class Mistral
        {
            public const string Model = "mistral_model";
            public const string ApiKey = "mistral_api_key";
            public const string RequestTemplate = "mistral_request_template";
        }

        public static class XAi
        {
            public const string Model = "xai_model";
            public const string ApiKey = "xai_api_key";
            public const string RequestTemplate = "xai_request_template";
        }

        public static class LibreTranslate
        {
            public const string Url = "libretranslate_url";
            public const string ApiKey = "libretranslate_api_key";
        }

        public const string SourceLanguages = "source_languages";
        public const string TargetLanguages = "target_languages";
        public const string AiPrompt = "ai_prompt";
        public const string AiUserPrompt = "ai_user_prompt";
        public const string ProofreadPrompt = "proofread_prompt";
        public const string ProofreadUserPrompt = "proofread_user_prompt";
        public const string AiContextBefore = "ai_context_before";
        public const string AiContextAfter = "ai_context_after";
        public const string AiContextUseTranslated = "ai_context_use_translated";
        public const string FixOverlappingSubtitles = "fix_overlapping_subtitles";
        public const string StripSubtitleFormatting = "strip_subtitle_formatting";
        public const string PreserveLineBreaks = "preserve_line_breaks";
        public const string BilingualSubtitleOutput = "bilingual_subtitle_output";
        public const string AddTranslatorInfo = "add_translator_info";
        public const string UseBatchTranslation = "use_batch_translation";
        public const string MaxBatchSize = "max_batch_size";
        public const string UseSubtitleTagging = "use_subtitle_tagging";
        public const string RemoveLanguageTag = "remove_language_tag";
        public const string SubtitleTag = "subtitle_tag";
        public const string IgnoreCaptions = "ignore_captions";
        public const string SubtitleNamingTemplate = "subtitle_naming_template";

        /// <summary>
        /// Fallback used when the <see cref="SubtitleNamingTemplate"/> setting is
        /// absent or blank — reproduces the historical "{base}.{language}" naming,
        /// so no migration is needed for instances that never customized it.
        /// {原名} expands to the cleaned base name (caption/language segments
        /// stripped), {语言} to the target language tag.
        /// </summary>
        public const string DefaultSubtitleNamingTemplate = "{原名}.{语言}";
        public const string RequestTimeout = "request_timeout";
        public const string MaxRetries = "max_retries";
        public const string RetryDelay = "retry_delay";
        public const string RetryDelayMultiplier = "retry_delay_multiplier";
        public const string NavigateToDetailsOnRequest = "navigate_to_details_on_request";
        public const string LanguageCodeFormat = "language_code_format";
    }

    public static class Automation
    {
        public const string AutomationEnabled = "automation_enabled";
        public const string TranslationSchedule = "translation_schedule";
        public const string MaxTranslationsPerRun = "max_translations_per_run";
        public const string TranslationCycle = "translation_cycle";
        public const string MovieSchedule = "movie_schedule";
        public const string ShowSchedule = "show_schedule";
        public const string MovieAgeThreshold = "movie_age_threshold";
        public const string ShowAgeThreshold = "show_age_threshold";

        /// <summary>
        /// JSON list of user-created recurring job definitions from the schedule
        /// screen「＋ new job」. Persisted so Initialize() can re-register them
        /// after a restart. Each entry: { jobId, jobType, cron, enabled }.
        /// </summary>
        public const string CustomRecurringJobs = "custom_recurring_jobs";

        /// <summary>
        /// JSON list of built-in recurring job ids the user disabled on the
        /// schedule screen. Initialize() skips them; an empty/missing value
        /// means every built-in job runs (the historical default).
        /// </summary>
        public const string DisabledRecurringJobs = "disabled_recurring_jobs";
    }

    public static class SubtitleValidation
    {
        public const string MaxFileSizeBytes = "subtitle_validation_maxfilesizebytes";
        public const string MaxSubtitleLength = "subtitle_validation_maxsubtitlelength";
        public const string MinSubtitleLength = "subtitle_validation_minsubtitlelength";
        public const string MinDurationMs = "subtitle_validation_mindurationms";
        public const string MaxDurationSecs = "subtitle_validation_maxdurationsecs";
        public const string ValidateSubtitles = "subtitle_validation_enabled";
    }

    public static class Authentication
    {
        public const string AuthEnabled = "auth_enabled";
        public const string ApiKey = "api_key";

        /// <summary>
        /// Dead setting (P2-4): seeded by the M0001 baseline but read by
        /// nothing — the API-key gate is the presence of a non-empty
        /// <see cref="ApiKey"/> value, independent of auth_enabled. The row
        /// is deliberately kept in existing databases (touching the squashed
        /// baseline would break verification against already-migrated
        /// installs); no code path may reference this key.
        /// </summary>
        [Obsolete("Dead setting: never read; the api_key value itself gates API-key auth.")]
        public const string ApiKeyEnabled = "api_key_enabled";

        /// <summary>
        /// "true" while the seeded default admin still uses the well-known
        /// admin/admin credentials; flipped to "false" by the first successful
        /// password change. Seeded "false" by M0002 for existing databases.
        /// </summary>
        public const string MustChangePassword = "must_change_password";
    }
}
