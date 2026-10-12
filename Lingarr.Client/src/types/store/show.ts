import { IFilter, IIncludeSummary, IPagedResult, IShow } from '@/types'

export interface IUseShowStore {
    shows: IPagedResult<IShow>
    filter: IFilter
    selectedShows: IShow[]
    selectAll: boolean
    includeSummary: IIncludeSummary
}
