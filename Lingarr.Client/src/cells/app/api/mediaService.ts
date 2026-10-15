import { AxiosStatic } from 'axios'
import { IMediaService, IIncludeSummary, MediaType } from '@/types'
import { buildUrl } from '@/cells/kernel/utils/string'

const service = (http: AxiosStatic, resource = '/api/media'): IMediaService => ({
    movies<T>(
        pageNumber: number,
        searchQuery: string,
        orderBy: string,
        ascending: boolean
    ): Promise<T> {
        return http
            .get(
                buildUrl(`${resource}/movies`, {
                    pageNumber: pageNumber,
                    searchQuery: searchQuery,
                    orderBy: orderBy,
                    ascending: ascending
                })
            )
            .then((response) => response.data)
    },
    library<T>(params: {
        pageNumber: number
        searchQuery: string
        sortBy: string
        ascending: boolean
        mediaType?: string | null
        state?: string | null
        targetLanguage?: string | null
        id?: number | null
    }): Promise<T> {
        return http
            .get(
                buildUrl(`${resource}/library`, {
                    pageNumber: params.pageNumber,
                    searchQuery: params.searchQuery,
                    orderBy: params.sortBy,
                    ascending: params.ascending,
                    mediaType: params.mediaType ?? '',
                    state: params.state ?? '',
                    targetLanguage: params.targetLanguage ?? '',
                    id: params.id ?? ''
                })
            )
            .then((response) => response.data)
    },
    estimate<T>(items: { mediaType: string; id: number }[], targetLanguage: string): Promise<T> {
        return http
            .post(`${resource}/estimate`, { items, targetLanguage })
            .then((response) => response.data)
    },
    shows<T>(
        pageNumber: number,
        searchQuery: string,
        orderBy: string,
        ascending: boolean
    ): Promise<T> {
        return http
            .get(
                buildUrl(`${resource}/shows`, {
                    pageNumber: pageNumber,
                    searchQuery: searchQuery,
                    orderBy: orderBy,
                    ascending: ascending
                })
            )
            .then((response) => response.data)
    },
    include<T>(mediaType: MediaType, id: number, include: boolean): Promise<T> {
        return http
            .post(`${resource}/include`, { mediaType: mediaType, id: id, include: include })
            .then((response) => response.data)
    },
    includeAll<T>(mediaType: MediaType, include: boolean): Promise<T> {
        return http
            .post(`${resource}/include/all`, { mediaType: mediaType, include: include })
            .then((response) => response.data)
    },
    includeSummary(mediaType: MediaType): Promise<IIncludeSummary> {
        return http
            .get(buildUrl(`${resource}/include/summary`, { mediaType }))
            .then((response) => response.data)
    },
    threshold<T>(mediaType: MediaType, id: number, hours: string): Promise<T> {
        return http
            .post(`${resource}/threshold`, { mediaType: mediaType, id: id, hours: hours })
            .then((response) => response.data)
    },
    /// Ground truth behind the coverage chips: every external subtitle file
    /// next to one media item, dual-language names reporting all languages.
    mediaSubtitles<T>(mediaType: MediaType, id: number): Promise<T> {
        return http
            .get(`${resource}/${mediaType}/${id}/subtitles`)
            .then((response) => response.data)
    }
})

export const mediaService = (axios: AxiosStatic): IMediaService => {
    return service(axios)
}
