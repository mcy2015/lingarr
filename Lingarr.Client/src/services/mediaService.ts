import { AxiosStatic } from 'axios'
import { IMediaService, IIncludeSummary, MediaType } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/media'): IMediaService => ({
    movies<T>(
        pageNumber: number,
        searchQuery: string,
        orderBy: string,
        ascending: boolean
    ): Promise<T> {
        return http
            .get(
                `${resource}/movies`.addParams({
                    pageNumber: pageNumber,
                    searchQuery: searchQuery,
                    orderBy: orderBy,
                    ascending: ascending
                })
            )
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
                `${resource}/shows`.addParams({
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
            .get(`${resource}/include/summary`.addParams({ mediaType: mediaType }))
            .then((response) => response.data)
    },
    threshold<T>(mediaType: MediaType, id: number, hours: string): Promise<T> {
        return http
            .post(`${resource}/threshold`, { mediaType: mediaType, id: id, hours: hours })
            .then((response) => response.data)
    }
})

export const mediaService = (axios: AxiosStatic): IMediaService => {
    return service(axios)
}
