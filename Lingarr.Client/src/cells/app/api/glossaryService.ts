import { AxiosStatic } from 'axios'
import { IGlossaryBulkEntry, IGlossaryBulkResult, IGlossaryService, ITranslationGlossary } from '@/types'

const service = (http: AxiosStatic, resource = '/api/glossary'): IGlossaryService => ({
    getAll(): Promise<ITranslationGlossary[]> {
        return http.get(`${resource}`).then((response) => response.data)
    },
    create<T>(entry: Partial<ITranslationGlossary>): Promise<T> {
        return http.post(`${resource}`, entry).then((response) => response.data)
    },
    update<T>(id: number, entry: Partial<ITranslationGlossary>): Promise<T> {
        return http.put(`${resource}/${id}`, entry).then((response) => response.data)
    },
    remove<T>(id: number): Promise<T> {
        return http.delete(`${resource}/${id}`).then((response) => response.data)
    },
    bulkImport(entries: IGlossaryBulkEntry[]): Promise<IGlossaryBulkResult> {
        return http
            .post(`${resource}/bulk`, {
                entries: entries.map((entry) => ({
                    sourceTerm: entry.sourceTerm,
                    targetTerm: entry.targetTerm,
                    matchMode: entry.matchMode
                }))
            })
            .then((response) => response.data)
    }
})

export const glossaryService = (axios: AxiosStatic): IGlossaryService => {
    return service(axios)
}
