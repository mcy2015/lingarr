// @vitest-environment happy-dom
// P1-1 overview trend card: dual-series micro bar chart normalization,
// legend peaks computed from real data, empty state and unknown-cost path.
import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import { mount } from '@vue/test-utils'
import TrendCard from '@/cells/dashboard/components/TrendCard.vue'
import { buildTrendSeries } from '@/cells/dashboard/services/trendSeries'
import { IDailyTrendStatistics } from '@/types'
import en from '@/i18n/locales/en.json'

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const mountCard = (
    trend: IDailyTrendStatistics | null,
    month?: { written: number; cost: number | null } | null
) => mount(TrendCard, { props: { trend, month }, global: { plugins: [i18n] } })

const makeTrend = (overrides: Partial<IDailyTrendStatistics> = {}): IDailyTrendStatistics => ({
    days: [
        { date: '2026-10-10', writtenCount: 4, cost: 0.08 },
        { date: '2026-10-11', writtenCount: 11, cost: 0.2 },
        { date: '2026-10-12', writtenCount: 0, cost: 0 }
    ],
    totalWritten: 15,
    totalCost: 0.28,
    ratesConfigured: true,
    ...overrides
})

describe('buildTrendSeries', () => {
    it('normalizes each series to its own peak', () => {
        const series = buildTrendSeries(makeTrend())

        // written peak 11 → 100%, 4 → 36%; cost peak 0.2 → 100%, 0.08 → 40%
        expect(series.peakWritten).toBe(11)
        expect(series.peakCost).toBeCloseTo(0.2)
        expect(series.bars[1].writtenPct).toBe(100)
        expect(series.bars[0].writtenPct).toBe(36)
        expect(series.bars[0].costPct).toBe(40)
        expect(series.hasData).toBe(true)
    })

    it('treats any unknown day cost as an unknown cost series', () => {
        const series = buildTrendSeries(
            makeTrend({
                days: [
                    { date: '2026-10-10', writtenCount: 2, cost: null },
                    { date: '2026-10-11', writtenCount: 1, cost: 0.1 }
                ],
                totalCost: null,
                ratesConfigured: false
            })
        )

        expect(series.peakCost).toBeNull()
        expect(series.bars.every((bar) => bar.costPct === null || bar.costPct === 0)).toBe(true)
    })

    it('reports no data for an all-zero window', () => {
        const series = buildTrendSeries(
            makeTrend({
                days: [{ date: '2026-10-10', writtenCount: 0, cost: 0 }]
            })
        )

        expect(series.hasData).toBe(false)
    })
})

describe('TrendCard', () => {
    it('renders one group per day with proportional bar heights', () => {
        const wrapper = mountCard(makeTrend())

        const groups = wrapper.findAll('[data-test="trend-bars"] > div')
        expect(groups).toHaveLength(3)

        const peak = groups[1]
        expect(peak.attributes('title')).toContain('11')
        const bars = peak.findAll('div[class*=bg-]')
        expect(bars[0].attributes('style')).toContain('height: 100%')
    })

    it('shows the legend peak note computed from the data', () => {
        const wrapper = mountCard(makeTrend())

        expect(wrapper.text()).toContain(en.overview.trend.title)
        expect(wrapper.text()).toContain('peak 11 files')
        expect(wrapper.text()).toContain('$0.20')
    })

    it('shows the empty state for a zero window', () => {
        const wrapper = mountCard(
            makeTrend({
                days: [{ date: '2026-10-10', writtenCount: 0, cost: 0 }],
                totalWritten: 0,
                totalCost: 0
            })
        )

        expect(wrapper.text()).toContain(en.overview.trend.empty)
        expect(wrapper.find('[data-test="trend-bars"]').exists()).toBe(false)
    })

    it('hides the cost series and explains why when usage is unpriced', () => {
        const wrapper = mountCard(
            makeTrend({
                days: [
                    { date: '2026-10-10', writtenCount: 3, cost: null },
                    { date: '2026-10-11', writtenCount: 1, cost: null }
                ],
                totalWritten: 4,
                totalCost: null,
                ratesConfigured: false
            })
        )

        // write-back bars still render, cost bars do not
        const groups = wrapper.findAll('[data-test="trend-bars"] > div')
        expect(groups).toHaveLength(2)
        expect(groups[0].findAll('div[class*=bg-]')).toHaveLength(1)
        expect(wrapper.text()).toContain(en.overview.trend.costUnknown)
        expect(wrapper.text()).toContain(en.overview.trend.metaUnknownCost.replace('{count}', '4'))
    })

    it('renders the dash meta while the trend has not loaded', () => {
        const wrapper = mountCard(null)

        expect(wrapper.text()).toContain('—')
        expect(wrapper.text()).toContain(en.overview.trend.empty)
    })

    it('appends the month-to-date segment from the pipeline stats (QA P3-3)', () => {
        const wrapper = mountCard(makeTrend(), { written: 42, cost: 3.5 })

        expect(wrapper.text()).toContain(en.overview.trend.monthMeta.replace('{count}', '42').replace('{cost}', '$3.50'))
    })

    it('renders the month segment with an unknown cost when rates are missing', () => {
        const wrapper = mountCard(makeTrend(), { written: 7, cost: null })

        expect(wrapper.text()).toContain(en.overview.trend.monthMetaUnknownCost.replace('{count}', '7'))
        expect(wrapper.text()).not.toContain('$3.50')
    })

    it('renders the month segment for an empty database as real zeros', () => {
        const wrapper = mountCard(
            makeTrend({ days: [{ date: '2026-10-12', writtenCount: 0, cost: 0 }], totalWritten: 0, totalCost: 0 }),
            { written: 0, cost: 0 }
        )

        expect(wrapper.text()).toContain(en.overview.trend.monthMeta.replace('{count}', '0').replace('{cost}', '$0.00'))
    })

    it('omits the month segment while the pipeline stats have not loaded', () => {
        const wrapper = mountCard(makeTrend(), null)

        expect(wrapper.text()).not.toContain(en.overview.trend.monthMeta.slice(0, 10))
        expect(wrapper.text()).toContain(en.overview.trend.meta.replace('{count}', '15').replace('{cost}', '$0.28'))
    })
})
