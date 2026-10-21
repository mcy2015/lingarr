import axios from 'axios'
import type {
    ISubtitleSearchOutcome,
    ISubtitleDownloadResponse,
    ISubtitleBacklog,
    ISubtitleHistoryResponse,
    ISubtitleProviderHealthState
} from '@/types/services'

/// Season-pack fan-out contract: the download response also carries
/// `filesLanded` — how many files the download wrote (1 for an ordinary
/// candidate, N when the picked candidate was a whole-season archive landed
/// per episode). Optional here because the shared wire-shape mirror in
/// types/services predates it; the modal defaults a missing value to 1.
export interface ISeasonPackDownloadResponse extends ISubtitleDownloadResponse {
    filesLanded?: number
}

/// Locked contract with the backend: `provider` is the single source key
/// (required — the backend rejects provider-less fan-in bodies with 400),
/// `mediaType` is the wire form "movie" | "episode", and `query` is the
/// optional keyword override (omit when empty).
interface SearchRequest {
    provider: string
    mediaType: string
    mediaId: number
    languages: string[]
    query?: string
}

interface DownloadRequest {
    cacheKey: string
    candidateIndex: number
    mediaType: string
    mediaId: number
    source: 'Manual' | 'Auto' | 'Webhook'
    /// true = write the file only; false/omitted = let the server's
    /// translation adjudication decide whether to queue a translation.
    skipTranslation?: boolean
}

/// Keyset filters of the history screen (screen 13). Every field is
/// optional; undefined entries are omitted from the query string so the
/// first page is a bare GET.
export interface SubtitleHistoryQuery {
    /// Opaque cursor of the previous page's last row; omit on page 1.
    cursor?: string
    /// Page size, server clamps to 1..100 (default 50).
    limit?: number
    /// "movie" | "episode" (case-insensitive on the server).
    mediaType?: string
    /// Exact source-key filter.
    provider?: string
    /// SubtitleDownloadResult enum name.
    result?: string
    /// SubtitleDownloadSource enum name (B02): "Upgrade" isolates the
    /// upgrade-round rows on the history screen.
    source?: string
}

class SubtitleSearchService {
    async search<T = ISubtitleSearchOutcome>(
        request: SearchRequest,
        signal?: AbortSignal
    ): Promise<T> {
        const response = await axios.post<T>('/api/subtitle-search/search', request, { signal })
        return response.data
    }

    async download<T = ISeasonPackDownloadResponse>(
        request: DownloadRequest,
        signal?: AbortSignal
    ): Promise<T> {
        const response = await axios.post<T>('/api/subtitle-search/download', request, { signal })
        return response.data
    }

    async testConnection<T = { ok: boolean; latencyMs: number; message: string }>(
        provider: string
    ): Promise<T> {
        const response = await axios.post<T>(`/api/subtitle-search/test/${provider}`)
        return response.data
    }

    async backlog<T = ISubtitleBacklog>(): Promise<T> {
        const response = await axios.get<T>('/api/subtitle-search/backlog')
        return response.data
    }

    /// One keyset page of the subtitle download history, newest first.
    /// The next page is requested by passing back the previous response's
    /// nextCursor; a null cursor means the end.
    async history<T = ISubtitleHistoryResponse>(
        query: SubtitleHistoryQuery = {},
        signal?: AbortSignal
    ): Promise<T> {
        const response = await axios.get<T>('/api/subtitle-search/history', { params: query, signal })
        return response.data
    }

    /// All registered subtitle sources in registration order, with their
    /// setting field manifests (secret fields carry the API keys/tokens the
    /// settings card edits through the encrypted setting channel) and the
    /// per-source cooldown health (design ⑧: cooldownUntil non-null while
    /// the source sits out rounds).
    async getProviders<T = ISubtitleProviderHealthState[]>(): Promise<T> {
        const response = await axios.get<T>('/api/subtitle-search/providers')
        return response.data
    }

    async pin(mediaType: string, mediaId: number, pinned: boolean): Promise<void> {
        await axios.post('/api/subtitle-search/pin', { mediaType, mediaId, pinned })
    }
}

export const subtitleSearchService = new SubtitleSearchService()
