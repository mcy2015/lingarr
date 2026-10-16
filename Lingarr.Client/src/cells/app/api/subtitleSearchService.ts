import axios from 'axios'
import type {
    ISubtitleSearchOutcome,
    ISubtitleDownloadResponse,
    ISubtitleBacklog
} from '@/types/services'
import type { ISubtitleProviderManifest } from '@/types/plugin'

interface SearchRequest {
    mediaType: string
    mediaId: number
    languages: string[]
    provider?: string
}

interface DownloadRequest {
    cacheKey: string
    candidateIndex: number
    mediaType: string
    mediaId: number
    source: 'Manual' | 'Auto' | 'Webhook'
}

class SubtitleSearchService {
    async search<T = ISubtitleSearchOutcome>(request: SearchRequest): Promise<T> {
        const response = await axios.post<T>('/api/subtitle-search/search', request)
        return response.data
    }

    async download<T = ISubtitleDownloadResponse>(request: DownloadRequest): Promise<T> {
        const response = await axios.post<T>('/api/subtitle-search/download', request)
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
