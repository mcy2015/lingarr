// @vitest-environment happy-dom
// SubtitleSearchHistoryPage(第 13 屏):GET /api/subtitle-search/history 的
// keyset 消费方。覆盖:行渲染(时间/标题链接/源/语言/来源/结果徽章/文件名)、
// 媒体已删除行的孤儿回退、筛选变化重置回第一页、「加载更多」按游标续页、
// 末页无续页按钮、空态与首屏失败可重试。全部文案断言针对 en.json 真实键。
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import SubtitleSearchHistoryPage from '@/cells/library/pages/SubtitleSearchHistoryPage.vue'
import { ISubtitleHistoryItem, ISubtitleHistoryResponse } from '@/types'
import en from '@/i18n/locales/en.json'

const historyMock = vi.hoisted(() => vi.fn())
const getProvidersMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: {
        history: historyMock,
        getProviders: getProvidersMock
    }
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

// ── fixtures ─────────────────────────────────────────────────────
function historyItem(overrides: Partial<ISubtitleHistoryItem> = {}): ISubtitleHistoryItem {
    return {
        id: 1,
        mediaType: 'Movie',
        mediaId: 7,
        mediaTitle: 'Movie',
        provider: 'opensubtitles',
        language: 'zh',
        source: 'Manual',
        result: 'Succeeded',
        error: null,
        fileName: 'Movie.2019.WEBDL-1080p.zh.srt',
        createdAt: '2026-10-15T08:30:00Z',
        ...overrides
    }
}

const firstPage: ISubtitleHistoryResponse = {
    items: [
        historyItem(),
        historyItem({
            id: 2,
            mediaType: 'Episode',
            mediaId: 3,
            showId: 5,
            mediaTitle: 'Silo S01E02 The Common',
            provider: 'assrt',
            language: 'en',
            source: 'Auto',
            result: 'Failed',
            error: 'assrt rejected the request',
            fileName: null,
            createdAt: '2026-10-15T08:10:00Z'
        })
    ],
    nextCursor: 'cursor-1'
}

// ── scaffold ─────────────────────────────────────────────────────
interface MountedHistory {
    wrapper: VueWrapper
    router: ReturnType<typeof createRouter>
}

async function mountHistoryPage(): Promise<MountedHistory> {
    const pinia = createPinia()
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/subtitle-history', name: 'subtitle-history', component: SubtitleSearchHistoryPage },
            // detail drill-downs the title links target
            {
                path: '/library/show/:id',
                name: 'media-detail-show',
                component: { template: '<div />' },
                props: true
            },
            {
                path: '/library/movie/:id',
                name: 'media-detail-movie',
                component: { template: '<div />' },
                props: true
            }
        ]
    })
    await router.push('/subtitle-history')
    await router.isReady()

    const wrapper = mount(SubtitleSearchHistoryPage, {
        global: { plugins: [pinia, i18n, router] }
    })
    await flushPromises()

    return { wrapper, router }
}

describe('SubtitleSearchHistoryPage', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        historyMock.mockReset().mockResolvedValue(firstPage)
        getProvidersMock.mockReset().mockResolvedValue([
            { provider: 'opensubtitles' },
            { provider: 'assrt' },
            { provider: 'gestdown' }
        ])
    })

    it('renders the first page rows with every column', async () => {
        const { wrapper } = await mountHistoryPage()

        expect(historyMock).toHaveBeenCalledTimes(1)
        expect(historyMock).toHaveBeenCalledWith({ limit: 50, mediaType: undefined, provider: undefined, result: undefined })

        const rows = wrapper.findAll('tbody tr')
        expect(rows).toHaveLength(2)

        const movieRow = rows[0]
        expect(movieRow.text()).toContain('Movie')
        expect(movieRow.text()).toContain('opensubtitles')
        expect(movieRow.text()).toContain('zh')
        expect(movieRow.text()).toContain(en.subHistory.source.Manual)
        expect(movieRow.text()).toContain('Movie.2019.WEBDL-1080p.zh.srt')
        expect(wrapper.find('[data-test="sub-history-result-1"]').text()).toBe(
            en.subHistory.result.Succeeded
        )

        const episodeRow = rows[1]
        expect(episodeRow.text()).toContain('Silo S01E02 The Common')
        // failed attempt: nothing landed, the failure reason rides the badge
        expect(wrapper.find('[data-test="sub-history-result-2"]').attributes('title')).toBe(
            'assrt rejected the request'
        )
        expect(wrapper.find('[data-test="sub-history-result-2"]').text()).toBe(
            en.subHistory.result.Failed
        )
        // no fileName → honest dash cell, not a fake name
        expect(episodeRow.text()).toContain('—')

        // the count meta reports the rows actually loaded (pluralized copy)
        expect(wrapper.find('[data-test="subtitle-history-count"]').text()).toContain('2')
    })

    it('links movie and episode titles to their detail routes', async () => {
        const { wrapper, router } = await mountHistoryPage()

        const movieLink = wrapper.find('[data-test="sub-history-media-1"]')
        expect(movieLink.attributes('href')).toBe('/library/movie/7')
        await movieLink.trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('media-detail-movie')
        expect(router.currentRoute.value.path).toBe('/library/movie/7')

        // the episode row of the same page targets the show detail route via
        // the OWNING SHOW id — never the episode id (the 4087-vs-200 jump bug)
        const episodeLink = wrapper.find('[data-test="sub-history-media-2"]')
        expect(episodeLink.attributes('href')).toBe('/library/show/5')
        await episodeLink.trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('media-detail-show')
        expect(router.currentRoute.value.path).toBe('/library/show/5')
    })

    it('never routes an episode row with its episode id, and drops the link when the show is gone', async () => {
        historyMock.mockResolvedValue({
            items: [
                historyItem({
                    id: 9,
                    mediaType: 'Episode',
                    mediaId: 4087,
                    showId: 200,
                    mediaTitle: 'Some Show S02E07'
                }),
                historyItem({
                    id: 10,
                    mediaType: 'Episode',
                    mediaId: 4088,
                    showId: null,
                    mediaTitle: 'Orphan Episode S01E01'
                })
            ],
            nextCursor: null
        })
        const { wrapper } = await mountHistoryPage()

        // the production bug shape: episode id 4087 must link to show 200
        const link = wrapper.find('[data-test="sub-history-media-9"]')
        expect(link.attributes('href')).toBe('/library/show/200')
        expect(link.attributes('href')).not.toContain('4087')

        // titled episode whose show no longer resolves → no dead-id link
        expect(wrapper.find('[data-test="sub-history-media-10"]').exists()).toBe(false)
    })

    it('renders removed media rows without a link', async () => {
        historyMock.mockResolvedValue({
            items: [historyItem({ mediaTitle: null, mediaId: 999999 })],
            nextCursor: null
        })
        const { wrapper } = await mountHistoryPage()

        expect(wrapper.find('[data-test="sub-history-media-1"]').exists()).toBe(false)
        expect(wrapper.find('tbody tr').text()).toContain(en.subHistory.mediaRemoved)
        expect(wrapper.find('tbody a').exists()).toBe(false)
    })

    it('renders the CooldownSkipped verdict with its own label and filter option', async () => {
        // assrt 493 closure: a refused click inside the rate-limit window is
        // its own outcome — labeled distinctly and never shown as Failed.
        historyMock.mockResolvedValue({
            items: [
                historyItem({
                    id: 11,
                    result: 'CooldownSkipped',
                    error: 'assrt throttled the download (HTTP 493).',
                    fileName: null
                })
            ],
            nextCursor: null
        })
        const { wrapper } = await mountHistoryPage()

        expect(wrapper.find('[data-test="sub-history-result-11"]').text()).toBe(
            en.subHistory.result.CooldownSkipped
        )
        // the badge stays honest even when the row carries the source's
        // original throttle message (tooltip, not label)
        expect(wrapper.find('[data-test="sub-history-result-11"]').attributes('title')).toBe(
            'assrt throttled the download (HTTP 493).'
        )
        expect(wrapper.find('[data-test="sub-history-result-11"]').text()).not.toBe(
            en.subHistory.result.Failed
        )

        // the result filter offers its own bucket — a CooldownSkipped row
        // only matches CooldownSkipped, never the Failed filter
        const options = wrapper
            .find('[data-test="sub-history-filter-result"]')
            .findAll('option')
            .map((option) => (option.element as HTMLOptionElement).value)
        expect(options).toContain('CooldownSkipped')
    })

    it('resets to the first page when any filter changes', async () => {
        const { wrapper } = await mountHistoryPage()
        expect(historyMock).toHaveBeenCalledTimes(1)

        await wrapper.find('[data-test="sub-history-filter-media-type"]').setValue('episode')
        await flushPromises()
        expect(historyMock).toHaveBeenLastCalledWith({
            limit: 50,
            mediaType: 'episode',
            provider: undefined,
            result: undefined
        })

        // provider options come from the providers endpoint (data-driven)
        const providerSelect = wrapper.find('[data-test="sub-history-filter-provider"]')
        const providerValues = providerSelect
            .findAll('option')
            .map((option) => (option.element as HTMLOptionElement).value)
        expect(providerValues).toEqual(['', 'opensubtitles', 'assrt', 'gestdown'])

        await providerSelect.setValue('assrt')
        await flushPromises()
        expect(historyMock).toHaveBeenLastCalledWith({
            limit: 50,
            mediaType: 'episode',
            provider: 'assrt',
            result: undefined
        })

        await wrapper.find('[data-test="sub-history-filter-result"]').setValue('Failed')
        await flushPromises()
        expect(historyMock).toHaveBeenLastCalledWith({
            limit: 50,
            mediaType: 'episode',
            provider: 'assrt',
            result: 'Failed'
        })

        // every filter round-trip re-issued page 1 — no cursor ever sent
        const calls = historyMock.mock.calls as unknown[][]
        expect(calls.every((call) => (call[0] as { cursor?: string }).cursor === undefined)).toBe(true)
    })

    it('appends the next keyset page when「加载更多」is clicked', async () => {
        const secondPage: ISubtitleHistoryResponse = {
            items: [historyItem({ id: 3, mediaId: 8, mediaTitle: 'Dune' })],
            nextCursor: null
        }
        const { wrapper } = await mountHistoryPage()

        const more = wrapper.find('[data-test="sub-history-load-more"]')
        expect(more.exists()).toBe(true)

        historyMock.mockResolvedValueOnce(secondPage)
        await more.trigger('click')
        await flushPromises()

        expect(historyMock).toHaveBeenLastCalledWith({ cursor: 'cursor-1', limit: 50 })
        expect(wrapper.findAll('tbody tr')).toHaveLength(3)
        // last page: no cursor left → the footer disappears entirely
        expect(wrapper.find('[data-test="sub-history-load-more"]').exists()).toBe(false)
    })

    it('hides the load-more affordance on a single-page result', async () => {
        historyMock.mockResolvedValue({ items: [historyItem()], nextCursor: null })
        const { wrapper } = await mountHistoryPage()

        expect(wrapper.find('[data-test="sub-history-load-more"]').exists()).toBe(false)
    })

    it('renders the honest empty state', async () => {
        historyMock.mockResolvedValue({ items: [], nextCursor: null })
        const { wrapper } = await mountHistoryPage()

        const empty = wrapper.find('[data-test="subtitle-history-empty"]')
        expect(empty.exists()).toBe(true)
        expect(empty.text()).toBe(en.subHistory.empty)
        expect(wrapper.find('tbody').exists()).toBe(false)
    })

    it('shows the retryable error row when the first load fails, then recovers', async () => {
        historyMock.mockReset().mockResolvedValue(firstPage).mockRejectedValueOnce(new Error('boom'))
        const { wrapper } = await mountHistoryPage()

        const error = wrapper.find('[data-test="subtitle-history-error"]')
        expect(error.exists()).toBe(true)
        expect(wrapper.text()).toContain(en.subHistory.loadFailed)
        expect(wrapper.find('tbody').exists()).toBe(false)

        await wrapper.find('[data-test="subtitle-history-retry"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="subtitle-history-error"]').exists()).toBe(false)
        expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    })
})
