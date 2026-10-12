import { IFilter, IIncludeSummary, IMovie, IPagedResult } from '@/types'

export interface IUseMovieStore {
    movies: IPagedResult<IMovie>
    filter: IFilter
    includeSummary: IIncludeSummary
}
