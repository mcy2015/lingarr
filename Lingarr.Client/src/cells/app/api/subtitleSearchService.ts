import axios from 'axios'
import type {
    ISubtitleSearchOutcome,
    ISubtitleDownloadResponse,
    ISubtitleBacklog
} from '@/types/services'
import type { ISubtitleProviderManifest } from '@/types/plugin'

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

class SubtitleSearchService {
    async search<T = ISubtitleSearchOutcome>(
        request: SearchRequest,
        signal?: AbortSignal
    ): Promise<T> {
        const response = await axios.post<T>('/api/subtitle-search/search', request, { signal })
        return response.data
    }

    async download<T = ISubtitleDownloadResponse>(
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

    /// All registered subtitle sources in registration order, with their
    /// setting field manifests (secret fields carry the API keys/tokens the
    /// settings card edits through the encrypted setting channel).
    async getProviders<T = ISubtitleProviderManifest[]>(): Promise<T> {
        const response = await axios.get<T>('/api/subtitle-search/providers')
        return response.data
    }

    async pin(mediaType: string, mediaId: number, pinned: boolean): Promise<void> {
        await axios.post('/api/subtitle-search/pin', { mediaType, mediaId, pinned })
    }
}

export const subtitleSearchService = new SubtitleSearchService()
