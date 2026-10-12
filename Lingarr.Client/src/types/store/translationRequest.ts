import { IActiveTranslation, IFilter, IPagedResult, ITranslationRequest } from '@/types'

export interface IUseTranslationRequestStore {
    activeTranslations: IActiveTranslation[]
    translationRequests: IPagedResult<ITranslationRequest>
    filter: IFilter
    statusCounts: Record<string, number>
    proofreadSupported: boolean
}
