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
