import { IDailyTrendPoint, IDailyTrendStatistics } from '@/types'

/// One normalized bar group of the overview trend chart (P1-1): heights are
/// percentages of each series' own peak, so write-backs and cost stay
/// readable side by side even at very different scales.
export interface TrendBarGroup {
    date: string
    writtenPct: number
    costPct: number | null
}

export interface TrendSeries {
    bars: TrendBarGroup[]
    peakWritten: number
    peakCost: number | null
    hasData: boolean
}

export function buildTrendSeries(trend: IDailyTrendStatistics | null): TrendSeries {
    const days: IDailyTrendPoint[] = trend?.days ?? []
    // a single unknown day makes the whole cost series unknown — partial
    // pricing would read as a real total
    const costKnown = days.length > 0 && days.every((day) => day.cost !== null)
    const peakWritten = days.reduce((max, day) => Math.max(max, day.writtenCount), 0)
    const peakCost = costKnown
        ? days.reduce((max, day) => Math.max(max, day.cost ?? 0), 0)
        : null

    const hasData = days.some((day) => day.writtenCount > 0 || (day.cost ?? 0) > 0)
    const bars = days.map((day) => ({
        date: day.date,
        writtenPct: peakWritten > 0 ? Math.round((day.writtenCount / peakWritten) * 100) : 0,
        // null peak = unknown cost window: no cost bar renders at all
        costPct:
            peakCost === null
                ? null
                : peakCost > 0
                  ? Math.round(((day.cost ?? 0) / peakCost) * 100)
                  : 0
    }))

    return { bars, peakWritten, peakCost, hasData }
}
