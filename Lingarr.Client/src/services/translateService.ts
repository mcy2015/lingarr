import { AxiosStatic } from 'axios'
import { ILanguage, IProofreadLineRequest, ISubtitle, ITranslateService, MediaType } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/translate'): ITranslateService => ({
    translateSubtitle<T>(
        mediaId: number,
        subtitle: ISubtitle,
        source: string,
        target: ILanguage,
        mediaType: MediaType
    ): Promise<T> {
        return http
            .post(`${resource}/file`, {
                mediaId: mediaId,
                subtitlePath: subtitle.path,
                subtitleFormat: subtitle.format,
                sourceLanguage: source,
                targetLanguage: target.code,
                mediaType: mediaType
            })
            .then((response) => response.data)
    },
    bulkTranslate<T>(mediaIds: number[], targetLanguage: string, mediaType: MediaType): Promise<T> {
        return http
            .post(`${resource}/bulk`, { mediaIds, targetLanguage, mediaType })
            .then((response) => response.data)
    },
    getLanguages<T>(): Promise<T> {
        return http.get(`${resource}/languages`).then((response) => response.data)
    },
    proofreadStatus<T>(): Promise<T> {
        return http.get(`${resource}/proofread/status`).then((response) => response.data)
    },
    proofreadLine<T>(request: IProofreadLineRequest): Promise<T> {
        return http.post(`${resource}/proofread`, request).then((response) => response.data)
    }
})

export const translateService = (axios: AxiosStatic): ITranslateService => {
    return service(axios)
}
