import { AxiosStatic } from 'axios'
import { IProofreadLineApplyRequest, ITranslationRequest, ITranslationRequestService } from '@/types'
import { buildUrl } from '@/utils/string'

const service = (
    http: AxiosStatic,
    resource = '/api/translationRequest'
): ITranslationRequestService => ({
    get<T>(id: number): Promise<T> {
        return http.get(`${resource}/${id}`).then((response) => response.data)
    },
    getActiveTranslations<T>(): Promise<T> {
        return http.get(`${resource}/active`).then((response) => response.data)
    },
    requests<T>(
        pageNumber: number,
        searchQuery: string,
        orderBy: string,
        ascending: boolean,
        status?: string | null
    ): Promise<T> {
        return http
            .get(
                buildUrl(`${resource}/requests`, {
                    pageNumber: pageNumber,
                    searchQuery: searchQuery,
                    orderBy: orderBy,
                    ascending: ascending,
                    status: status ?? ''
                })
            )
            .then((response) => response.data)
    },
    statusCounts<T>(): Promise<T> {
        return http.get(`${resource}/counts`).then((response) => response.data)
    },
    pause<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http.post(`${resource}/pause`, translationRequest).then((response) => response.data)
    },
    resumePaused<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http
            .post(`${resource}/resume-paused`, translationRequest)
            .then((response) => response.data)
    },
    cancel<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http.post(`${resource}/cancel`, translationRequest).then((response) => response.data)
    },
    convert<T>(id: number, bilingual: boolean): Promise<T> {
        return http.post(`${resource}/convert`, { id, bilingual }).then((response) => response.data)
    },
    convertAll<T>(bilingual: boolean): Promise<T> {
        return http.post(`${resource}/convert/all`, { bilingual }).then((response) => response.data)
    },
    remove<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http.post(`${resource}/remove`, translationRequest).then((response) => response.data)
    },
    retry<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http.post(`${resource}/retry`, translationRequest).then((response) => response.data)
    },
    resume<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http.post(`${resource}/resume`, translationRequest).then((response) => response.data)
    },
    proofread<T>(translationRequest: ITranslationRequest): Promise<T> {
        return http
            .post(`${resource}/proofread`, translationRequest)
            .then((response) => response.data)
    },
    applyProofreadLine<T>(request: IProofreadLineApplyRequest): Promise<T> {
        return http.post(`${resource}/proofread/line`, request).then((response) => response.data)
    }
})

export const translationRequestService = (axios: AxiosStatic): ITranslationRequestService => {
    return service(axios)
}
