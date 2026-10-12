import { IFilter, IIncludeSummary, IPagedResult, IShow } from '@/types'

export interface IUseShowStore {
    shows: IPagedResult<IShow>
    filter: IFilter
    includeSummary: IIncludeSummary
}
