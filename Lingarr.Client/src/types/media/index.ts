import { ISubtitle } from '@/types'

interface IBaseEntity {
    id: number
    createdAt: Date
    updatedAt: Date
}

export interface IShow extends IBaseEntity {
    sonarrId: number
    title: string
    path: string
    dateAdded?: Date | null
    images: IImage[]
    seasons: ISeason[]
    includeInTranslation: boolean
    translationAgeThreshold: string
}

export interface IMovie extends IBaseEntity {
    radarrId: number
    title: string
    fileName: string
    path: string
    dateAdded?: Date | null
    images: IImage[]
    subtitles?: ISubtitle[]
    includeInTranslation: boolean
    translationAgeThreshold: string
}

export interface ITranslationRequest {
    id: number
    jobId: string
    mediaId?: number | null
    updatedAt?: string
    title: string
    sourceLanguage: string
    targetLanguage: string
    subtitleToTranslate?: string
    translatedSubtitle?: string
    isBilingual?: boolean
    mediaType: MediaType
    status: TranslationStatus
    source: TranslationRequestSource
    progress: number
    serviceType?: string | null
    startedAt?: string | null
    retryCount?: number | null
    completedAt?: string | null
    errorMessage?: string | null
    stackTrace?: string | null
    createdAt?: string
}

export interface ITranslationRequestDetail extends ITranslationRequest {
    events: ITranslationRequestEvent[]
    lines: ISubtitleLineComparison[]
}

export interface ITranslationRequestEvent {
    id: number
    status: TranslationStatus
    eventType?: TranslationEventType | null
    provider?: string | null
    message?: string | null
    createdAt: string
}

export const TRANSLATION_EVENT_TYPE = {
    CREATED: 'Created',
    ENQUEUED: 'Enqueued',
    STARTED: 'Started',
    PROVIDER_FAILED: 'ProviderFailed',
    DEGRADED: 'Degraded',
    BATCH_COMPLETED: 'BatchCompleted',
    COMPLETED: 'Completed',
    FAILED: 'Failed',
    WRITEBACK: 'Writeback'
} as const

export type TranslationEventType =
    (typeof TRANSLATION_EVENT_TYPE)[keyof typeof TRANSLATION_EVENT_TYPE]

export interface ISubtitleLineComparison {
    position: number
    source: string
    target: string
    service?: string | null
    startTimeMs?: number | null
    endTimeMs?: number | null
}

export interface ILineTranslated {
    id: number
    position: number
    source: string
    target: string
    service?: string | null
}

export interface ILineProofread {
    id: number
    position: number
    source: string
    target: string
    service?: string | null
}

export interface IProofreadStatus {
    supported: boolean
    service?: string
}

export interface IProofreadLineRequest {
    sourceLine: string
    translatedLine: string
    sourceLanguage: string
    targetLanguage: string
}

export interface IProofreadLineApplyRequest {
    id: number
    position: number
    target: string
    origin: ProofreadLineOrigin
}

export const PROOFREAD_LINE_ORIGIN = {
    PROOFREAD: 'Proofread',
    MANUAL: 'Manual'
} as const

export type ProofreadLineOrigin =
    (typeof PROOFREAD_LINE_ORIGIN)[keyof typeof PROOFREAD_LINE_ORIGIN]

export interface IActiveTranslation {
    mediaId: number | null
    mediaType: MediaType
    status: TranslationStatus
}

export interface IRequestProgress {
    id: number
    jobId: string
    status: TranslationStatus
    progress: number
    completed: boolean
    completedAt?: string | null
    errorMessage?: string | null
    stackTrace?: string | null
}

export interface IImage {
    id: number
    type: string
    path: string
    showId?: number | null
    show?: IShow | null
    movieId?: number | null
    movie?: IMovie | null
}

export interface ISeason extends IBaseEntity {
    seasonNumber: number
    path: string
    episodes: IEpisode[]
    showId: number
    show: IShow
    includeInTranslation: boolean
}

export interface IEpisode extends IBaseEntity {
    sonarrId: number
    episodeNumber: number
    title: string
    fileName?: string | null
    path?: string | null
    seasonId: number
    season: ISeason
    includeInTranslation: boolean
    /// Persisted exemption flag (requirement card A / B2 gap counts): the
    /// subtitle-need rule judged this episode as not needing subtitles.
    /// Absent on legacy payloads reads as false.
    noSubtitleNeeded?: boolean
    /// Media-file detail batch (episodeFile payload): the episode table's
    /// 质量/大小 columns. Absent on legacy payloads → column shows "—".
    quality?: string | null
    sizeBytes?: number | null
    videoCodec?: string | null
    audioCodec?: string | null
    audioChannels?: number | null
    resolution?: string | null
    runtimeMinutes?: number | null
}

export interface IPagedResult<T> {
    items: T[]
    totalCount: number
    pageNumber: number
    pageSize: number
}

export interface IIncludeSummary {
    included: number
    total: number
}

export const MEDIA_TYPE = {
    MOVIE: 'Movie',
    SHOW: 'Show',
    SEASON: 'Season',
    EPISODE: 'Episode'
} as const

export type MediaType = (typeof MEDIA_TYPE)[keyof typeof MEDIA_TYPE]

export const TRANSLATION_STATUS = {
    PENDING: 'Pending',
    INPROGRESS: 'InProgress',
    COMPLETED: 'Completed',
    FAILED: 'Failed',
    CANCELLED: 'Cancelled',
    INTERRUPTED: 'Interrupted',
    PAUSED: 'Paused'
} as const

export type TranslationStatus = (typeof TRANSLATION_STATUS)[keyof typeof TRANSLATION_STATUS]

export const TRANSLATION_SOURCE = {
    LIBRARY: 'Library',
    IMPORT: 'Import'
} as const

export type TranslationRequestSource = (typeof TRANSLATION_SOURCE)[keyof typeof TRANSLATION_SOURCE]

export enum TRANSLATION_ACTIONS {
    CANCEL,
    REMOVE,
    RETRY,
    RESUME,
    PROOFREAD,
    PAUSE,
    RESUME_PAUSED
}

export type MediaLibraryTrackState = 'complete' | 'partial' | 'none'

export interface ISubtitleTrackSummary {
    language: string
    have: number
    total: number
    state: MediaLibraryTrackState
    /// B07 forced dimension: how many episodes carry a forced-only (caption
    /// "forced") subtitle in this language. Presented inside the same
    /// per-language chip — `have`/`state` stay the FULL coverage verdict (a
    /// forced-only language reads state "none"). Optional so pre-B07 payloads
    /// keep typing as "no forced files".
    forcedHave?: number
}

export const MEDIA_TRANSLATION_STATE = {
    NONE: 'None',
    QUEUED: 'Queued',
    IN_PROGRESS: 'InProgress',
    COMPLETED: 'Completed',
    FAILED: 'Failed'
} as const

export type MediaTranslationState =
    (typeof MEDIA_TRANSLATION_STATE)[keyof typeof MEDIA_TRANSLATION_STATE]

/// One row of the library matrix (server gap-audit A4): track coverage from
/// the statistics scan plus request-derived state and last activity.
export interface IMediaLibraryItem {
    id: number
    mediaType: MediaType
    title: string
    dateAdded?: string | null
    seasonCount: number
    episodeCount: number
    tracks: ISubtitleTrackSummary[]
    state: MediaTranslationState
    completedCount: number
    failedCount: number
    lastActivityAt?: string | null
    includeInTranslation: boolean
    translationAgeThreshold?: number | null
    show?: IShow | null
    subtitleFiles?: ISubtitle[] | null
    /// Requirement card A: normalized audio language codes detected on the
    /// media's container (e.g. ['zh', 'en']). null = audio languages unknown
    /// (nothing scanned / und tags only). Optional so pre-A2 payloads that
    /// predate the field keep typing as "unknown".
    audioLanguages?: string[] | null
    /// Requirement card A: effective「无需字幕」exemption — the audio
    /// already covers the target language or the user marked it so. Absent
    /// (undefined) reads as false: no exemption.
    needsNoSubtitles?: boolean
    /// Requirement card A: the manual subtitle-need mark. true = forced
    /// "needs subtitles", false = forced "not needed", null/undefined =
    /// automatic (the audio rule decides). Drives the detail page badge.
    audioOverride?: boolean | null
    /// B08 per-media target-language override: null/undefined = follow the
    /// global target_languages setting (pre-B08 behavior, also what list
    /// rows and legacy payloads read as); [] = the explicit「无目标」state
    /// (the media wants no subtitles — reversible any time); otherwise the
    /// exact codes the need rules and coverage chips resolve against. Only
    /// the detail (single-item) projection populates it. Show rows carry
    /// the series-level override their episodes inherit.
    targetLanguagesOverride?: string[] | null
    /// Option B: the work's original language (TMDb via *arr) — a guess
    /// shown as a muted hint while the real audio languages are unknown.
    suggestedAudioLanguage?: string | null
    /// Library B2: release year from *arr. Shows carry the series year.
    year?: number | null
    /// Library B2: IMDb id ("tt…"); null when the payload had none.
    imdbId?: string | null
    /// Library B2: release quality label ("WEBDL-1080p"). Movie rows only —
    /// show projections leave it null (a series has no single file).
    quality?: string | null
    /// Library B2: file size in bytes. Movie rows only.
    sizeBytes?: number | null
    /// Library B2: IMDb user rating; null when *arr has no real score.
    imdbRating?: number | null
    /// Media-file detail batch: TMDb plot summary (*arr movie/series
    /// overview) for the detail page's 简介 block.
    overview?: string | null
    /// Media-file detail batch: *arr MediaCover path of the synced poster;
    /// loaded via /api/image/{movie|show}{path}. Null when none was synced.
    posterPath?: string | null
    /// Media-file detail batch: video file name without extension and its
    /// directory (movie rows only) — the file card's 文件名/所在目录 lines.
    fileName?: string | null
    path?: string | null
    /// Media-file detail batch: movie-file mediaInfo facts (movie rows only).
    videoCodec?: string | null
    audioCodec?: string | null
    audioChannels?: number | null
    resolution?: string | null
    runtimeMinutes?: number | null
}

export interface ITranslationEstimate {
    characters: number
    provider: string
    cost: number | null
}
