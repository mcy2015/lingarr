import { AxiosStatic } from 'axios'
import {
    IBulkTranslateSummary,
    ILanguage,
    IProofreadLineRequest,
    IServiceLanguageResponse,
    ISubtitle,
    ITranslateEmbeddedTrackRequest,
    ITranslateService,
    ITranslationPreviewRequest,
    ITranslationPreviewResponse,
    ITranslationTestResponse,
    MediaType
} from '@/types'

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
    /// R11: the bulk endpoint answers with a created/skipped summary instead
    /// of an empty body, so silent skips become visible to the caller.
    bulkTranslate(
        mediaIds: number[],
        targetLanguage: string,
        mediaType: MediaType
    ): Promise<IBulkTranslateSummary> {
        return http
            .post(`${resource}/bulk`, { mediaIds, targetLanguage, mediaType })
            .then((response) => response.data as IBulkTranslateSummary)
    },
    translateEmbeddedTrack<T>(request: ITranslateEmbeddedTrackRequest): Promise<T> {
        return http.post(`${resource}/embedded-track`, request).then((response) => response.data)
    },
    getLanguages<T>(): Promise<T> {
        return http.get(`${resource}/languages`).then((response) => response.data)
    },
    getServiceLanguages(serviceType: string): Promise<IServiceLanguageResponse> {
        return http.get(`${resource}/languages/${serviceType}`).then((response) => response.data)
    },
    getHealth() {
        return http.get(`${resource}/health`).then((response) => response.data)
    },
    proofreadStatus<T>(): Promise<T> {
        return http.get(`${resource}/proofread/status`).then((response) => response.data)
    },
    proofreadLine<T>(request: IProofreadLineRequest): Promise<T> {
        return http.post(`${resource}/proofread`, request).then((response) => response.data)
    },
    testConnection(serviceType: string): Promise<ITranslationTestResponse> {
        return http.post(`${resource}/test/${serviceType}`).then((response) => response.data)
    },
    previewTranslation(request: ITranslationPreviewRequest): Promise<ITranslationPreviewResponse> {
        return http.post(`${resource}/preview`, request).then((response) => response.data)
    }
})

export const translateService = (axios: AxiosStatic): ITranslateService => {
    return service(axios)
}
