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

        /// <summary>
        /// Plex server base URL (B03), plain text — the same shape as the
        /// *arr URLs (host:port, no path). Seeded empty by StartupService.
        /// </summary>
        public const string PlexUrl = "plex_url";

        /// <summary>
        /// Plex authentication token (B03), sent as the X-Plex-Token header —
        /// never folded into a URL query string. Encrypted at rest, seeded
        /// empty; only the reveal endpoint ever returns it.
        /// </summary>
        public const string PlexToken = "plex_token";

        /// <summary>
        /// Master switch of the post-landing Plex library refresh (B03);
        /// seeded "false" — an outbound integration never runs implicitly.
        /// Gates the enqueue side (download pipeline / translation writeback)
        /// and is re-read by the job itself against settings-change races.
        /// </summary>
        public const string PlexRefreshEnabled = "plex_refresh_enabled";
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

    /// <summary>
    /// Remote audio-language identification (walk-through option C): an
    /// OpenAI-compatible transcription endpoint consulted on demand from
    /// the media detail page; the detected spoken language fills
    /// audio_languages for containers without language tags.
    /// </summary>
    public static class AudioIdentification
    {
        public const string BaseUrl = "audio_id_url";
        public const string ApiKey = "audio_id_api_key";
        public const string Model = "audio_id_model";
        /// <summary>mkvpropedit tag write-back switch (option D), off by default.</summary>
        public const string Writeback = "audio_id_writeback";
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
        /// F2b: whether the automated translation job also consumes text-based
        /// embedded subtitle tracks as translation sources ("false" by default
        /// — manual initiation from the media detail page is always allowed).
        /// </summary>
        public const string ConsumeEmbeddedTracks = "consume_embedded_tracks";

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
        public const string CleanupSchedule = "cleanup_schedule";
        public const string StatisticsSchedule = "statistics_schedule";

        /// <summary>Cron of the ffprobe embedded subtitle track scan (F2a).</summary>
        public const string EmbeddedTrackSchedule = "embedded_track_schedule";

        /// <summary>
        /// Cron of the automated subtitle-search batch (subtitle-search M3,
        /// design ⑤). Seeded "0 5 * * *" by M0008 — one hour after the 04:00
        /// sync wave so a round sees same-night syncs.
        /// </summary>
        public const string SubtitleSearchSchedule = "subtitle_search_schedule";

        /// <summary>
        /// Cron of the subtitle upgrade re-search round (B02). Seeded
        /// "0 4 * * *" by M0014 — inside the 04:00 maintenance wave; the
        /// round only ever re-offers files landed inside the upgrade window.
        /// </summary>
        public const string SubtitleUpgradeSchedule = "subtitle_upgrade_schedule";

        /// <summary>
        /// Upper bound of media files one embedded track scan run may probe
        /// (audit P0-6). Already-scanned files are free (marker skip), so the
        /// next run resumes right after the last processed file. Values below
        /// 1 or a missing/blank setting fall back to
        /// <see cref="DefaultEmbeddedScanBatchLimit"/> — no migration needed.
        /// </summary>
        public const string EmbeddedScanBatchLimit = "embedded_scan_batch_limit";

        /// <summary>
        /// Fallback used when <see cref="EmbeddedScanBatchLimit"/> is absent,
        /// blank or unparsable — mirrors the SubtitleNamingTemplate fallback
        /// pattern (code default, no seeded row).
        /// </summary>
        public const int DefaultEmbeddedScanBatchLimit = 500;

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

    /// <summary>
    /// Settings of the subtitle-search feature (docs/subtitle-search-tech-
    /// design.zh-CN.md ④/⑧). The five scalar rows are seeded by migration
    /// M0008; the cooldown map is a JSON blob written by the orchestrator —
    /// no seeded row, absent means "no source is cooling down".
    /// </summary>
    public static class SubtitleSearch
    {
        /// <summary>Master switch of the automated batch; seeded "false".</summary>
        public const string Enabled = "subtitle_search_enabled";

        /// <summary>Single auto-download threshold (0–100); seeded "75".</summary>
        public const string AutoScore = "subtitle_search_auto_score";

        /// <summary>Whether downloaded non-SRT tracks are converted to SRT; seeded "true".</summary>
        public const string ConvertToSrt = "subtitle_search_convert_srt";

        /// <summary>Upper bound of media items one automated round processes; seeded "200".</summary>
        public const string BatchLimit = "subtitle_search_batch_limit";

        /// <summary>
        /// Whether an "en" download is enqueued for translation; seeded
        /// "true" (design ⑦ step ⑦).
        /// </summary>
        public const string AutoTranslate = "subtitle_search_auto_translate";

        /// <summary>
        /// JSON map of active per-source cooldowns (design ⑧):
        /// <c>{ "opensubtitles": { "until": "...", "reason": "RateLimited", "error": "..." } }</c>.
        /// Read at round start to skip cooled-down sources; written whenever a
        /// source call fails with a cooldown-worthy reason. 401/403 entries
        /// carry until=9999-12-31 (until credentials change).
        /// </summary>
        public const string SourceCooldowns = "subtitle_source_cooldowns";

        /// <summary>
        /// Chinese variant applied to subtitles after they land on disk
        /// ("none"/"simplified"/"traditional" — the import-option vocabulary);
        /// seeded "none" (B01): converting stored text never runs by default.
        /// </summary>
        public const string PostProcessChineseVariant = "subtitle_postprocess_chinese_variant";

        /// <summary>
        /// Master switch of the subtitle upgrade re-search round (B02);
        /// seeded "false" by M0014 — re-searching the library and REPLACING
        /// stored files never runs implicitly. Governs both the recurring
        /// registration (ScheduleService) and Execute()'s short-circuit.
        /// </summary>
        public const string UpgradeEnabled = "subtitle_upgrade_enabled";

        /// <summary>
        /// Minimum score gain an upgrade candidate must beat the old file by
        /// (B02); seeded "3". The effective auto line of one target is
        /// max(subtitle_search_auto_score, old score + this margin), so a
        /// same-score candidate never replaces and the round cannot loop
        /// (margin ≥ 1 keeps every replacement strictly score-increasing).
        /// </summary>
        public const string UpgradeScoreMargin = "subtitle_upgrade_score_margin";

        /// <summary>
        /// How recent (in days) a succeeded download must be for the upgrade
        /// round to re-offer it (B02); seeded "14". Bounds the candidate set:
        /// a 10w library's first round only sees the window's tail, the rest
        /// digests day by day as the window scrolls.
        /// </summary>
        public const string UpgradeWindowDays = "subtitle_upgrade_window_days";

        /// <summary>
        /// Whether hearing-impaired (SDH) candidates win score ties (B07);
        /// seeded "false". A pure ordering preference — the score itself and
        /// the auto-download threshold never see this flag, so the default
        /// keeps the pre-B07 ranking byte-identical.
        /// </summary>
        public const string PreferHearingImpaired = "subtitle_search_prefer_hearing_impaired";
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
