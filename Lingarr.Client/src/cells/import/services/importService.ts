import { AxiosStatic } from 'axios'
import {
    IImportEstimateResponse,
    IImportService,
    INormalizeExecuteItem,
    INormalizeExecuteResponse,
    INormalizePreviewResponse
} from '@/types'

const service = (http: AxiosStatic, resource = '/api/subtitle'): IImportService => ({
    scanPath: async (path: string) => {
        const response = await http.post(`${resource}/scan-path`, { path })
        return response.data
    },

    /// Import cost estimate lives on the import controller, not the
    /// subtitle controller — its own base path.
    estimate: async (params: {
        files: { name: string; characters: number }[]
    }): Promise<IImportEstimateResponse> => {
        const response = await http.post('/api/import/estimate', params)
        return response.data
    },

    importPaths: async <T>(params: {
        paths: string[]
        sourceLanguage: string
        targetLanguage: string
        translate: boolean
        mediaType?: 'Movie' | 'Show'
        mediaId?: number
        season?: number
        chineseConvert?: string
        isBilingual?: boolean
    }): Promise<T> => {
        const response = await http.post(`${resource}/import-paths`, {
            paths: params.paths,
            sourceLanguage: params.sourceLanguage,
            targetLanguage: params.targetLanguage,
            translate: params.translate,
            mediaType: params.mediaType,
            mediaId: params.mediaId,
            season: params.season,
            chineseConvert: params.chineseConvert ?? 'none',
            isBilingual: params.isBilingual ?? false
        })
        return response.data
    },

    parseFilenames: async (filenames: string[]) => {
        const response = await http.post(`${resource}/parse-filenames`, { filenames })
        return response.data
    },

    /// F3 subtitle normalize: preview computes the rename diff for one
    /// movie/show without writing; execute renames the confirmed items.
    normalizePreview: async (
        mediaType: 'Movie' | 'Show',
        mediaId: number
    ): Promise<INormalizePreviewResponse> => {
        const response = await http.post(`${resource}/normalize/preview`, { mediaType, mediaId })
        return response.data
    },

    normalizeExecute: async (params: {
        mediaType: 'Movie' | 'Show'
        mediaId: number
        items: INormalizeExecuteItem[]
    }): Promise<INormalizeExecuteResponse> => {
        const response = await http.post(`${resource}/normalize/execute`, {
            mediaType: params.mediaType,
            mediaId: params.mediaId,
            items: params.items
        })
        return response.data
    },

    importFiles: async (
        files: File[],
        sourceLanguage: string,
        targetLanguage: string,
        translate: boolean,
        mediaType?: 'Movie' | 'Show',
        mediaId?: number,
        season?: number,
        chineseConvert?: string,
        isBilingual?: boolean
    ) => {
        const formData = new FormData()
        formData.append('isBilingual', String(isBilingual ?? false))
        files.forEach((file) => formData.append('files', file))
        formData.append('sourceLanguage', sourceLanguage)
        formData.append('targetLanguage', targetLanguage)
        formData.append('translate', String(translate))
        formData.append('chineseConvert', chineseConvert ?? 'none')
        if (mediaType && mediaId) {
            formData.append('mediaType', mediaType)
            formData.append('mediaId', String(mediaId))
            if (season !== undefined) {
                formData.append('season', String(season))
            }
        }

        const response = await http.post(`${resource}/import`, formData, {
            headers: { 'Content-Type': 'multipart/form-data' }
        })
        return response.data
    }
})

export const importService = (axios: AxiosStatic): IImportService => {
    return service(axios)
}
