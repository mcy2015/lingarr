import {
    DirectoryItem,
    IDailyTrendStatistics,
    ILanguage,
    IScannedSubtitleFile,
    ISettings,
    ISubtitle,
    ITranslationRequest,
    ITranslationRequestEvent,
    MediaType,
    IPathMapping,
    ILoginRequest,
    ILoginResponse,
    IAuthenticatedResponse,
    IChangePasswordRequest,
    IChangeUsernameRequest,
    IChangeUsernameResponse,
    IApiKeyResponse,
    IIncludeSummary,
    IPluginManifest,
    IPluginOptionsResponse,
    IPluginStatus,
    IPluginSummary,
    IProofreadLineApplyRequest,
    IProofreadLineRequest,
    IQueueDepth,
    IScheduleTimeZone
} from '@/types'

export interface IPluginService {
    list(): Promise<IPluginSummary[]>
    getManifest(provider: string): Promise<IPluginManifest>
    getStatus(provider: string): Promise<IPluginStatus>
    getOptions(endpoint: string): Promise<IPluginOptionsResponse>
}

export interface IAuthService {
    authenticated(): Promise<IAuthenticatedResponse>
    login(request: ILoginRequest): Promise<ILoginResponse>
    logout(): Promise<void>
    /// P0-3: change the logged-in user's password. The server revokes every
    /// session on success, so the caller must sign in again afterwards.
    changePassword(request: IChangePasswordRequest): Promise<ILoginResponse>
    /// Rename the logged-in user (current-password confirmed). Unlike a
    /// password change the session stays valid.
    changeUsername(request: IChangeUsernameRequest): Promise<IChangeUsernameResponse>
    generateApiKey(): Promise<IApiKeyResponse>
    /// Security walk fix: explicit reveal of the stored API key. Same guard
    /// as generation on the server (cookie sessions only); the only other
    /// place the plaintext key exists is the generate response.
    getApiKey(): Promise<IApiKeyResponse>
    /// P2-4: revoke the current API key. Requires a browser session on the
    /// server side; clears the stored key so subsequent API-key calls 401.
    revokeApiKey(): Promise<void>
    hasAnyUsers(): Promise<boolean>
}

export interface IMediaService {
    movies<T>(
        pageNumber: number,
        searchQuery: string,
        sortBy: string,
        ascending: boolean
    ): Promise<T>
    library<T>(params: {
        pageNumber: number
        searchQuery: string
        sortBy: string
        ascending: boolean
        mediaType?: string | null
        state?: string | null
        targetLanguage?: string | null
        id?: number | null
    }): Promise<T>
    estimate<T>(items: { mediaType: string; id: number }[], targetLanguage: string): Promise<T>
    shows<T>(
        pageNumber: number,
        searchQuery: string,
        sortBy: string,
        ascending: boolean
    ): Promise<T>
    include<T>(mediaType: MediaType, id: number, include: boolean): Promise<T>
    includeAll<T>(mediaType: MediaType, include: boolean): Promise<T>
    includeSummary(mediaType: MediaType): Promise<IIncludeSummary>
    threshold<T>(mediaType: MediaType, id: number, hours: string): Promise<T>
    /// External files plus embedded tracks of one media item (see
    /// IMediaSubtitlesResponse).
    mediaSubtitles<T>(mediaType: MediaType, id: number): Promise<T>
    /// Requirement card A/B: set the manual「需要字幕」override of one media
    /// item (needed=true → needs subtitles, needed=false → lifts the mark
    /// and re-exempts). The response carries the resulting effective state
    /// (see ISubtitleNeedResponse).
    subtitleNeed<T>(mediaType: MediaType, id: number, needed: boolean): Promise<T>
}

/// One external subtitle file next to a media item; languages carries every
/// language of a dual-language file (scjp → ['zh', 'ja']).
export interface IMediaSubtitleFile {
    fileName: string
    directory: string
    languages: string[]
    format: string
    bytes: number
}

/// One embedded subtitle stream inside the media container (ffprobe scan,
/// F2a). Graphic tracks (PGS…) carry isTextBased=false — display-only. Since
/// F2b text tracks can be translated; mediaId names the owning movie/episode
/// (show payloads aggregate over episodes, so each row must stay attributable
/// for per-track actions).
export interface IMediaEmbeddedTrack {
    mediaId: number
    streamIndex: number
    language: string | null
    rawLanguage: string | null
    title: string | null
    codec: string
    isTextBased: boolean
}

/// GET api/media/{mediaType}/{id}/subtitles payload: the external files the
/// card always listed plus the embedded tracks of the last scan.
/// embeddedScanned is false until the media's files were probed (or
/// confirmed missing) at least once.
export interface IMediaSubtitlesResponse {
    files: IMediaSubtitleFile[]
    embedded: IMediaEmbeddedTrack[]
    embeddedScanned: boolean
    /// Requirement card A: normalized audio language codes of the media's
    /// container (['zh', 'en']); null = unknown (nothing probed / und only).
    audioLanguages: string[] | null
}

/// POST api/media/{mediaType}/{id}/subtitle-need response: the effective
///「无需字幕」state after applying the requested「需要字幕」mark.
export interface ISubtitleNeedResponse {
    needsNoSubtitles: boolean
}

export interface ISettingService {
    getSetting<T>(key: string): Promise<T>
    getSettings<T>(keys: string[]): Promise<T>
    setSetting(key: string, value: string): Promise<void>
    setSettings(keys: ISettings): Promise<void>
    /// Security walk fix: the bulk encrypted read answers booleans only
    /// (configured per key) — the server never returns secret plaintext.
    /// T is the client-side states map (all-boolean values).
    getEncryptedSettingStates<T>(keys: string[]): Promise<T>
    /// Explicit per-key reveal: fetches one decrypted secret (cookie-session
    /// guarded endpoint, audited server-side). Empty string when unset.
    revealEncryptedSetting(key: string): Promise<string>
    setEncryptedSetting(key: string, value: string): Promise<void>
}

export interface ISubtitleService {
    collect<T>(path: string): Promise<T>
}

export interface IParsedSubtitleFilename {
    fileName: string
    season: number | null
    episode: number | null
    matchTier: 'Exact' | 'Guess' | 'None'
    languageTags: string[]
}

export interface IImportedSubtitleResult {
    fileName: string
    success: boolean
    mediaBound?: boolean
    duplicateOfRequestId?: number | null
    warning?: string | null
    savedPath?: string | null
    translationRequestId?: number | null
    error?: string | null
}

export interface IImportSubtitlesResponse {
    results: IImportedSubtitleResult[]
}

export interface IImportService {
    scanPath(path: string): Promise<IScannedSubtitleFile[]>
    importPaths<T>(params: {
        paths: string[]
        sourceLanguage: string
        targetLanguage: string
        translate: boolean
        mediaType?: 'Movie' | 'Show'
        mediaId?: number
        season?: number
        chineseConvert?: string
        isBilingual?: boolean
    }): Promise<T>
    parseFilenames(filenames: string[]): Promise<IParsedSubtitleFilename[]>
    importFiles(
        files: File[],
        sourceLanguage: string,
        targetLanguage: string,
        translate: boolean,
        mediaType?: 'Movie' | 'Show',
        mediaId?: number,
        season?: number,
        chineseConvert?: string,
        isBilingual?: boolean
    ): Promise<IImportSubtitlesResponse>
    /// Import cost estimate (PM analysis P0): the files queued for
    /// translation priced at the default provider's rate. cost is null
    /// when the provider has no rate ("unknown", not $0).
    estimate(params: {
        files: { name: string; characters: number }[]
    }): Promise<IImportEstimateResponse>
    /// F3 normalize preview: the rename plan for one movie's or show's
    /// external subtitles. Never writes.
    normalizePreview(
        mediaType: 'Movie' | 'Show',
        mediaId: number
    ): Promise<INormalizePreviewResponse>
    /// F3 normalize execution: renames the confirmed preview items.
    normalizeExecute(params: {
        mediaType: 'Movie' | 'Show'
        mediaId: number
        items: INormalizeExecuteItem[]
    }): Promise<INormalizeExecuteResponse>
}

export interface IImportEstimateResponse {
    cost: number | null
    currency: string
}

/// Skip reasons of the subtitle normalize preview (F3), mapped onto
/// import.normalize.reason.* keys. Mirrors the server's reason codes.
export type INormalizeSkipReason =
    | 'noVideoInDirectory'
    | 'ambiguousVideo'
    | 'noEpisodeMatch'
    | 'ambiguousEpisodeMatch'
    | 'languageUnknown'
    | 'dualLanguage'
    | 'targetConflict'
    | 'targetCollisionInBatch'
    | 'alreadyNormalized'

/// One planned rename of the normalize preview diff.
export interface INormalizePreviewItem {
    oldPath: string
    newPath: string
    oldName: string
    newName: string
    language: string
    mediaTitle: string
}

export interface INormalizeSkippedItem {
    path: string
    reason: INormalizeSkipReason
}

/// POST /api/subtitle/normalize/preview. Pure preview — writable=false with
/// error 'readOnly' | 'missingDirectory' is the blocking error state.
export interface INormalizePreviewResponse {
    items: INormalizePreviewItem[]
    skipped: INormalizeSkippedItem[]
    writable: boolean
    error: string | null
}

/// One user-confirmed rename sent back for execution.
export interface INormalizeExecuteItem {
    oldPath: string
    newPath: string
    language: string
    mediaTitle: string
}

export interface INormalizeExecuteResponse {
    renamed: number
    failed: { path: string; error: string }[]
}

export interface IVersionService {
    getVersion<T>(): Promise<T>
}

export interface ITranslateService {
    translateSubtitle<T>(
        mediaId: number,
        subtitle: ISubtitle,
        source: string,
        target: ILanguage,
        mediaType: MediaType
    ): Promise<T>
    bulkTranslate<T>(mediaIds: number[], targetLanguage: string, mediaType: MediaType): Promise<T>
    /// F2b: translate one text-based embedded subtitle track. The server
    /// extracts the stream with ffmpeg and creates a regular request.
    translateEmbeddedTrack<T>(request: ITranslateEmbeddedTrackRequest): Promise<T>
    getLanguages<T>(): Promise<T>
    getHealth(): Promise<ITranslationServiceHealth[]>
    /// Language capability of one named service (P1-3): its concrete
    /// language table, or "unrestricted" for AI-style services with none.
    getServiceLanguages(serviceType: string): Promise<IServiceLanguageResponse>
    proofreadStatus<T>(): Promise<T>
    proofreadLine<T>(request: IProofreadLineRequest): Promise<T>
    /// Active connection probe (PM analysis P0): one real minimal
    /// translation per provider, measured end to end.
    testConnection(serviceType: string): Promise<ITranslationTestResponse>
    /// Single-line translation preview through a named service's saved
    /// request template, with glossary hits and per-call cost.
    previewTranslation(request: ITranslationPreviewRequest): Promise<ITranslationPreviewResponse>
}

/// POST /api/translate/embedded-track body (F2b): translate one text-based
/// embedded subtitle stream of a movie/episode container.
export interface ITranslateEmbeddedTrackRequest {
    /// The media type that owns the container file ('Movie' | 'Episode').
    mediaType: MediaType
    /// Movie or episode id whose container carries the track.
    mediaId: number
    /// ffprobe stream index of the track inside the container.
    streamIndex: number
    /// Normalized source language of the track (informational — the scan row wins).
    sourceLanguage: string
    /// Target language code to translate into.
    targetLanguage: string
}

/// Inline result of the「测试连接」probe on the settings provider cards
/// (prototype screen-06). ok=false carries the provider failure message.
export interface ITranslationTestResponse {
    ok: boolean
    status: number | null
    latencyMs: number
    message: string
}

export interface ITranslationPreviewRequest {
    serviceType: string
    text: string
    sourceLanguage?: string
    targetLanguage?: string
}

export interface ITranslationPreviewGlossaryHit {
    sourceTerm: string
    targetTerm: string
    isLocked: boolean
}

/// Response of the「试译预览」card (prototype screen-10). Failures keep the
/// same shape with ok=false and a message.
export interface ITranslationPreviewResponse {
    ok: boolean
    translated: string | null
    latencyMs: number
    cost: number | null
    glossaryHits: ITranslationPreviewGlossaryHit[]
    message: string | null
}

/// Health row per provider for the overview card and settings provider
/// cards (server gap-audit A1): config status plus a rolling request window.
export interface ITranslationServiceHealth {
    provider: string
    displayName: string
    configured: boolean
    latencyMs: number | null
    successRate: number | null
    lastError: string | null
    lastRequestAt: string | null
    sampleCount: number
    monthlyCost: number | null
}

/// Language capability of one service (P1-3): "restricted" = concrete
/// language table, "unrestricted" = AI-style no gating, "unknown" = table
/// could not be retrieved (missing key / provider offline).
export interface IServiceLanguageResponse {
    serviceType: string
    status: 'restricted' | 'unrestricted' | 'unknown'
    languages: ILanguage[]
    message?: string | null
}

/// Overview pipeline strip payload (server gap-audit A2).
export interface IPipelineStatistics {
    mediaCount: number
    missingSubtitles: number
    queueDepth: number
    servicesOnline: number
    writtenThisMonth: number
    successRate30Days: number | null
    monthlyCost: number | null
}

/// One status-transition row of the overview activity feed (server gap-audit A3).
export interface ITranslationRequestEventSummary {
    id: number
    requestId: number
    requestTitle: string
    status: string
    eventType?: string | null
    provider?: string | null
    message: string | null
    createdAt: string
}

export interface ITranslationRequestService {
    get<T>(id: number): Promise<T>
    getActiveTranslations<T>(): Promise<T>
    requests<T>(
        pageNumber: number,
        searchQuery: string,
        sortBy: string,
        ascending: boolean,
        status?: string | null,
        mediaIds?: string | null,
        mediaParentId?: number | null
    ): Promise<T>
    statusCounts(): Promise<Record<string, number>>
    getRecentEvents(take: number): Promise<ITranslationRequestEventSummary[]>
    /// Full typed event timeline of one request, oldest first (P1-2).
    getEvents(id: number): Promise<ITranslationRequestEvent[]>
    pause<T>(translationRequest: ITranslationRequest): Promise<T>
    resumePaused<T>(translationRequest: ITranslationRequest): Promise<T>
    cancel<T>(translationRequest: ITranslationRequest): Promise<T>
    convert<T>(id: number, bilingual: boolean): Promise<T>
    convertAll<T>(bilingual: boolean): Promise<T>
    remove<T>(translationRequest: ITranslationRequest): Promise<T>
    retry<T>(translationRequest: ITranslationRequest): Promise<T>
    resume<T>(translationRequest: ITranslationRequest): Promise<T>
    proofread<T>(translationRequest: ITranslationRequest): Promise<T>
    applyProofreadLine<T>(request: IProofreadLineApplyRequest): Promise<T>
}

export interface IScheduleService {
    startJob<T>(jobName: string): Promise<T>
    recurringJobs<T>(): Promise<T>
    remove<T>(jobId: string): Promise<T>
    indexShows<T>(): Promise<T>
    indexMovies<T>(): Promise<T>
    /// Live per-queue Hangfire depth (screen-12 qdepth strip).
    depth(): Promise<IQueueDepth[]>
    /// Re-register a recurring job under the same id.
    enableJob<T>(jobId: string): Promise<T>
    /// Remove a recurring job registration; persisted across restarts.
    disableJob<T>(jobId: string): Promise<T>
    /// Update a recurring job's cron — the single edit entry for schedules;
    /// 400 invalid cron, 404 unknown id, 409 missing settings row.
    updateJobCron<T>(jobId: string, cron: string): Promise<T>
    /// Effective schedule time zone: crons are interpreted in it and
    /// schedule times displayed in it; warning is set when the server
    /// fell back to UTC.
    timeZone(): Promise<IScheduleTimeZone>
}

export interface IMappingService {
    getMappings(): Promise<IPathMapping[]>
    setMappings(mappings: IPathMapping[]): Promise<void>
    /// Ground truth: which stored media directories are not on this filesystem.
    status(): Promise<IMappingStatus>
    /// Tries an *arr-style path against the saved mappings (verdict, not 5xx).
    testMapping(path: string, mediaType: string): Promise<ITestMappingResult>
}

/// GET /api/mapping/status — reachability of the stored media directories.
export interface IMappingStatus {
    checked: number
    unreachable: { mediaType: string; path: string }[]
}

/// POST /api/mapping/test — one conversion through the sync pipeline's mapper.
export interface ITestMappingResult {
    ok: boolean
    matched: boolean
    mappedPath: string
    exists: boolean
}

export interface IDirectoryService {
    get(path: string): Promise<DirectoryItem[]>
}

export interface IStatisticsService {
    getStatistics<T>(): Promise<T>
    getDailyStatistics<T>(days?: number): Promise<T>
    getPipelineStatistics(): Promise<IPipelineStatistics>
    /// Daily write-back/cost series for the overview trend card (P1-1).
    getDailyTrendStatistics(days?: number): Promise<IDailyTrendStatistics>
    resetStatistics(): Promise<void>
}

export interface ILogsService {
    getStream(): EventSource
}

export interface IRequestTemplateService {
    getDefaults<T>(): Promise<T>
}

export interface ITranslationGlossary {
    id: number
    sourceTerm: string
    targetTerm: string
    note?: string | null
    isLocked: boolean
}

/// One row of a glossary CSV import (screen-06 C「导入 CSV」):
/// matchMode mirrors the per-entry enum — "locked" | "context".
export interface IGlossaryBulkEntry {
    sourceTerm: string
    targetTerm: string
    matchMode: 'locked' | 'context'
}

export interface IGlossaryBulkResult {
    imported: number
    skipped: number
}

export interface IGlossaryService {
    getAll(): Promise<ITranslationGlossary[]>
    create<T>(entry: Partial<ITranslationGlossary>): Promise<T>
    update<T>(id: number, entry: Partial<ITranslationGlossary>): Promise<T>
    remove<T>(id: number): Promise<T>
    bulkImport(entries: IGlossaryBulkEntry[]): Promise<IGlossaryBulkResult>
}

// ── Subtitle search ────────────────────────────────────────────
/// Mirrors the backend SubtitleCandidateResponse record verbatim: the
/// release line is `releaseInfo` (nullable — the UI falls back to
/// provider+externalId) and the size is a raw byte count the UI formats
/// itself. There is no `sizeLabel`/`release` field on the wire.
export interface ISubtitleCandidate {
    provider: string
    externalId: string
    language: string
    matchScore: number
    hashMatch: boolean
    forced: boolean
    hearingImpaired: boolean
    format: string
    releaseInfo: string | null
    uploader: string | null
    size: number | null
}

export interface ISubtitleSourceOutcome {
    provider: string
    reason: string
    error: string | null
}

export interface ISubtitleSearchOutcome {
    cacheKey: string
    candidates: ISubtitleCandidate[]
    sourceOutcomes: ISubtitleSourceOutcome[]
}

/// Mirrors the backend SubtitleDownloadResponse record: filePath non-empty
/// means the file landed; translationRequestId non-null means the download
/// also entered the translation queue; translationDuplicate flags an already
/// existing equivalent request.
export interface ISubtitleDownloadResponse {
    historyId: number
    filePath: string | null
    result: string
    error: string | null
    translationRequestId: number | null
    translationDuplicate: boolean
}

export interface ISubtitleBacklog {
    backlog: number
    nextRoundCount: number
    pinnedCount: number
    webhookBoostedCount: number
}
