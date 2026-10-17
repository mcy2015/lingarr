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
import { ITranslationRequest, ITranslationRequestEventSummary } from '@/types'
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
// double covers joinGroup/leaveGroup/on/off so the mount chain (and the
// per-request group re-syncs) never rejects.
const hubMock = vi.hoisted(() => ({
    joinGroup: vi.fn((): Promise<void> => Promise.resolve()),
    leaveGroup: vi.fn((): Promise<void> => Promise.resolve()),
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

async function mountDashboard(): Promise<{
    wrapper: VueWrapper
    router: ReturnType<typeof createRouter>
}> {
    const pinia = createPinia()
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: DashboardPage },
            // the 5th KPI deep-links into the subtitle history screen
            {
                path: '/subtitle-history',
                name: 'subtitle-history',
                component: { template: '<div />' }
            }
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
        translationRequestApi.requests.mockResolvedValue({
            items: [],
            totalCount: 0,
            pageNumber: 1,
            pageSize: 20
        })
        translationRequestApi.getRecentEvents.mockResolvedValue([])
        translateApi.getHealth.mockResolvedValue([])
        backlogMock.mockResolvedValue({
            backlog: 7,
            pinnedCount: 1,
            webhookBoostedCount: 2,
            nextRoundCount: 3
        })
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

describe('DashboardPage · 布局杂项(批E)', () => {
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
        translationRequestApi.requests.mockResolvedValue({
            items: [] as ITranslationRequest[],
            totalCount: 0,
            pageNumber: 1,
            pageSize: 20
        })
        translationRequestApi.getRecentEvents.mockResolvedValue([])
        translateApi.getHealth.mockResolvedValue([])
        backlogMock.mockResolvedValue({
            backlog: 7,
            pinnedCount: 1,
            webhookBoostedCount: 2,
            nextRoundCount: 3
        })
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('collapses the in-progress row to content height when no task is active (E1)', async () => {
        const { wrapper } = await mountDashboard()

        const row = wrapper.find('[data-test="active-row"]')
        expect(row.exists()).toBe(true)
        // no flex grow, no floor height — the card shrinks to its content
        // instead of stretching into a ~300px blank block
        expect(row.classes()).not.toContain('flex-[1.4]')
        expect(row.classes()).not.toContain('min-h-[170px]')
    })

    it('keeps the flexible in-progress row when tasks are active (E1)', async () => {
        translationRequestApi.requests.mockResolvedValue({
            // shallow mount stubs JobRow — the row only needs id for :key
            items: [{ id: 41, status: 'InProgress', progress: 5 }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDashboard()

        const row = wrapper.find('[data-test="active-row"]')
        expect(row.classes()).toContain('flex-[1.4]')
        expect(row.classes()).toContain('min-h-[170px]')
    })

    it('mirrors the KPI value-over-label order on the pipeline strip (E2)', async () => {
        const { wrapper } = await mountDashboard()

        const nodes = wrapper.findAll('[data-test^="pipe-node-"]')
        expect(nodes).toHaveLength(5)

        const kpiValue = wrapper.find('[data-test="kpi-cell-0"] .text-2xl')
        expect(kpiValue.exists()).toBe(true)

        // every pipeline node: value block first (KPI typography), label below
        for (const node of nodes) {
            const divs = node.findAll('div')
            expect(divs[0].classes()).toContain('text-2xl')
            expect(divs[0].classes()).toContain('font-semibold')
            expect(divs[1].classes()).toContain('text-xs')
            expect(divs[1].classes()).toContain('mt-0.5')
        }
        // same padding system as the KPI cells
        expect(nodes[0].classes()).toContain('px-4')
        expect(nodes[0].classes()).toContain('py-3')
        expect(wrapper.find('[data-test="kpi-cell-0"]').classes()).toContain('px-4')

        // the leading node still carries its real number and unit
        expect(nodes[0].findAll('div')[0].text()).toContain('10')
        expect(nodes[0].findAll('div')[0].text()).toContain(en.overview.pipe.mediaUnit)
        expect(nodes[0].findAll('div')[1].text()).toBe(en.overview.pipe.library)
    })

    it('separates the feed title from its status with spaces on both sides (E3)', async () => {
        translationRequestApi.getRecentEvents.mockResolvedValue([
            {
                id: 1,
                requestId: 1,
                requestTitle: 'Movie (2023)',
                status: 'Completed',
                eventType: null,
                provider: null,
                message: null,
                createdAt: '2026-10-13T15:41:22Z'
            } as ITranslationRequestEventSummary
        ])
        const { wrapper } = await mountDashboard()

        // wrapper.text() trims — assert on the raw textContent so the
        // leading space (the actual E3 fix) is verified
        const separator = wrapper.findAll('.text-secondary-content').find((node) =>
            (node.element.textContent ?? '').startsWith(' — ')
        )
        expect(separator).toBeDefined()
        // the localized status label follows the spaced dash, not glued to it
        expect(separator!.element.textContent).toContain(
            en.common.translationStatus.completed
        )
    })
})
