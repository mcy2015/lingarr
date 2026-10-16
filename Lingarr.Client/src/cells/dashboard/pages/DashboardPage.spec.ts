// @vitest-environment happy-dom
// DashboardPage · 第 5 KPI 格「字幕补缺」(观测面批次):backlog 聚合的四个
// 真实数字(积压主数 + 置顶/Webhook 提升/下轮副行,无配额),点击跳转
// /subtitle-history;backlog 读取失败时诚实降级为「—」,绝不显示编造数字。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import DashboardPage from '@/cells/dashboard/pages/DashboardPage.vue'
import en from '@/i18n/locales/en.json'

const statisticsApi = vi.hoisted(() => ({
    getPipelineStatistics: vi.fn(),
    getDailyTrendStatistics: vi.fn()
}))
const translationRequestApi = vi.hoisted(() => ({
    statusCounts: vi.fn(),
    requests: vi.fn(),
    getRecentEvents: vi.fn()
}))
const translateApi = vi.hoisted(() => ({ getHealth: vi.fn() }))
const backlogMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/statisticsService', () => ({
    statisticsService: () => statisticsApi
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => translationRequestApi
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => translateApi
}))

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: { backlog: backlogMock }
}))

// The page joins a live hub on mount; a real connection would negotiate
// over the network, so the composable is replaced wholesale — the hub
// double covers joinGroup/on/off so the mount chain never rejects.
const hubMock = vi.hoisted(() => ({
    joinGroup: vi.fn((): Promise<void> => Promise.resolve()),
    on: vi.fn(),
    off: vi.fn()
}))

vi.mock('@/cells/app/composables/useSignalR', () => ({
    useSignalR: () => ({
        state: { hubs: {} },
        connect: vi.fn((): Promise<typeof hubMock> => Promise.resolve(hubMock))
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const i18nFill = (key: string, values: Record<string, string | number>): string =>
    Object.entries(values).reduce(
        (text, [name, value]) => text.replaceAll(`{${name}}`, String(value)),
        key
    )

// ── scaffold ─────────────────────────────────────────────────────
// flushPromises relies on setImmediate/setTimeout which the fake
// timers own — advancing by 0ms yields the event loop the same way
// and additionally drains the microtask queue.
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

async function mountDashboard(): Promise<{ wrapper: VueWrapper; router: ReturnType<typeof createRouter> }> {
    const pinia = createPinia()
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: DashboardPage },
            // the 5th KPI deep-links into the subtitle history screen
            { path: '/subtitle-history', name: 'subtitle-history', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(DashboardPage, {
        global: { plugins: [pinia, i18n, router] },
        shallow: true
    })
    await settle()

    return { wrapper, router }
}

describe('DashboardPage · subtitle backlog KPI', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        statisticsApi.getPipelineStatistics.mockResolvedValue({
            mediaCount: 10,
            missingSubtitles: 63,
            queueDepth: 1,
            servicesOnline: 2,
            writtenThisMonth: 42,
            successRate30Days: null,
            monthlyCost: null
        })
        statisticsApi.getDailyTrendStatistics.mockResolvedValue({ days: [] })
        translationRequestApi.statusCounts.mockResolvedValue({ InProgress: 2, Pending: 12 })
        translationRequestApi.requests.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 })
        translationRequestApi.getRecentEvents.mockResolvedValue([])
        translateApi.getHealth.mockResolvedValue([])
        backlogMock.mockResolvedValue({ backlog: 7, pinnedCount: 1, webhookBoostedCount: 2, nextRoundCount: 3 })
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('renders the fifth KPI cell with the four backlog numbers and no quota figures', async () => {
        const { wrapper } = await mountDashboard()

        expect(backlogMock).toHaveBeenCalledTimes(1)

        const cells = wrapper.findAll('[data-test^="kpi-cell-"]')
        expect(cells).toHaveLength(5)

        const subtitleCell = cells[4]
        expect(subtitleCell.text()).toContain(en.overview.kpi.subtitleBacklog)
        // main number: the real backlog aggregate
        expect(subtitleCell.find('.text-2xl').text()).toBe('7')
        // sub line: pinned / webhook-boosted / next-round, all real counts
        expect(subtitleCell.text()).toContain(
            i18nFill(en.dashboard.subtitles.subline, { pinned: 1, boosted: 2, next: 3 })
        )
        // red line: no quota wording anywhere on the cell
        expect(subtitleCell.text().toLowerCase()).not.toContain('quota')
    })

    it('deep-links the subtitle cell to the history screen', async () => {
        const { wrapper, router } = await mountDashboard()

        await wrapper.find('[data-test="kpi-cell-4"]').trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('subtitle-history')
        expect(router.currentRoute.value.path).toBe('/subtitle-history')
    })

    it('degrades honestly to a dash when the backlog read fails', async () => {
        backlogMock.mockRejectedValueOnce(new Error('boom'))
        const { wrapper } = await mountDashboard()

        const subtitleCell = wrapper.find('[data-test="kpi-cell-4"]')
        expect(subtitleCell.find('.text-2xl').text()).toBe('—')
        // no sub line without data — an empty breakdown would read as zeros
        expect(subtitleCell.text()).not.toContain('Pinned')

        // the rest of the page kept its data (allSettled isolation)
        expect(wrapper.find('[data-test="kpi-cell-0"]').find('.text-2xl').text()).toBe('2')
    })
})
