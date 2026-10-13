import {
    DirectoryItem,
    ILanguage,
    IScannedSubtitleFile,
    ISettings,
    ISubtitle,
    ITranslationRequest,
    MediaType,
    IPathMapping,
    ILoginRequest,
    IApiKeyResponse,
    IIncludeSummary,
    IPluginManifest,
    IPluginOptionsResponse,
    IPluginStatus,
    IPluginSummary,
    IProofreadLineApplyRequest,
    IProofreadLineRequest
} from '@/types'

export interface IPluginService {
    list(): Promise<IPluginSummary[]>
    getManifest(provider: string): Promise<IPluginManifest>
    getStatus(provider: string): Promise<IPluginStatus>
    getOptions(endpoint: string): Promise<IPluginOptionsResponse>
}

export interface IAuthService {
    authenticated(): Promise<void>
    login(request: ILoginRequest): Promise<void>
    logout(): Promise<void>
    generateApiKey(): Promise<IApiKeyResponse>
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
}

export interface ISettingService {
    getSetting<T>(key: string): Promise<T>
    getSettings<T>(keys: string[]): Promise<T>
    setSetting(key: string, value: string): Promise<void>
    setSettings(keys: ISettings): Promise<void>
    getEncryptedSettings<T>(keys: string[]): Promise<T>
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
}

export interface IImportEstimateResponse {
    cost: number | null
    currency: string
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
    bulkTranslate<T>(
        mediaIds: number[],
        targetLanguage: string,
        mediaType: MediaType
    ): Promise<T>
    getLanguages<T>(): Promise<T>
    getHealth(): Promise<ITranslationServiceHealth[]>
    proofreadStatus<T>(): Promise<T>
    proofreadLine<T>(request: IProofreadLineRequest): Promise<T>
    /// Active connection probe (PM analysis P0): one real minimal
    /// translation per provider, measured end to end.
    testConnection(serviceType: string): Promise<ITranslationTestResponse>
    /// Single-line translation preview through a named service's saved
    /// request template, with glossary hits and per-call cost.
    previewTranslation(request: ITranslationPreviewRequest): Promise<ITranslationPreviewResponse>
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
        mediaIds?: string | null
    ): Promise<T>
    statusCounts(): Promise<Record<string, number>>
    getRecentEvents(take: number): Promise<ITranslationRequestEventSummary[]>
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
}

export interface IMappingService {
    getMappings(): Promise<IPathMapping[]>
    setMappings(mappings: IPathMapping[]): Promise<void>
}

export interface IDirectoryService {
    get(path: string): Promise<DirectoryItem[]>
}

export interface IStatisticsService {
    getStatistics<T>(): Promise<T>
    getDailyStatistics<T>(days?: number): Promise<T>
    getPipelineStatistics(): Promise<IPipelineStatistics>
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

export interface IGlossaryService {
    getAll(): Promise<ITranslationGlossary[]>
    create<T>(entry: Partial<ITranslationGlossary>): Promise<T>
    update<T>(id: number, entry: Partial<ITranslationGlossary>): Promise<T>
    remove<T>(id: number): Promise<T>
}
