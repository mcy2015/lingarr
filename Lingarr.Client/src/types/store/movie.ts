import { IFilter, IIncludeSummary, IMovie, IPagedResult } from '@/types'

export interface IUseMovieStore {
    movies: IPagedResult<IMovie>
    filter: IFilter
    selectedMovies: IMovie[]
    selectAll: boolean
    includeSummary: IIncludeSummary
}
