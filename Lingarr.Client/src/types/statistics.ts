export interface Statistics {
    totalLinesTranslated: number
    totalFilesTranslated: number
    totalCharactersTranslated: number
    totalMovies: number
    totalEpisodes: number
    totalSubtitles: number
    translationsByMediaType: Record<string, number>
    translationsByService: Record<string, number>
    subtitlesByLanguage: Record<string, number>
}

export interface DailyStatistic {
    date: string
    translationCount: number
}

/// One day of the overview trend series (P1-1): write-backs plus priced
/// usage. Cost is null while any usage in the window is unpriced.
export interface IDailyTrendPoint {
    date: string
    writtenCount: number
    cost: number | null
}

/// Payload of GET /api/statistics/trend/{days} (P1-1): contiguous,
/// zero-filled day buckets oldest-first.
export interface IDailyTrendStatistics {
    days: IDailyTrendPoint[]
    totalWritten: number
    totalCost: number | null
    ratesConfigured: boolean
}

export interface IProviderCost {
    provider: string
    requests: number
    characters: number
    cost: number | null
}

export interface ICostSummary {
    month: string
    totalCost: number | null
    ratesConfigured: boolean
    providers: IProviderCost[]
}
