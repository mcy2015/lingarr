// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { DOMWrapper, mount, VueWrapper } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import QueuePage from '@/cells/queue/pages/QueuePage.vue'
import useTranslationRequestStore from '@/cells/app/stores/translationRequest'
import { useUiStore } from '@/cells/app/stores/ui'
import { IPagedResult, ITranslationRequest, TRANSLATION_STATUS } from '@/types'

const translationRequestApi = vi.hoisted(() => ({
    requests: vi.fn(),
    statusCounts: vi.fn(),
    pause: vi.fn(),
    resumePaused: vi.fn(),
    cancel: vi.fn(),
    remove: vi.fn(),
    retry: vi.fn(),
    resume: vi.fn(),
    proofread: vi.fn(),
    applyProofreadLine: vi.fn(),
    getActiveTranslations: vi.fn(),
    convertAll: vi.fn()
}))
const translateApi = vi.hoisted(() => ({
    proofreadStatus: vi.fn()
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => translationRequestApi
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => translateApi
}))

const requestsMock = translationRequestApi.requests
const statusCountsMock = translationRequestApi.statusCounts
const proofreadStatusMock = translateApi.proofreadStatus

// The page joins a live hub on mount; a real connection would try to
// negotiate over the network, so the composable is replaced wholesale.
const signalrMocks = vi.hoisted(() => {
    const hub = {
        joinGroup: vi.fn((): Promise<void> => Promise.resolve()),
        leaveGroup: vi.fn((): Promise<void> => Promise.resolve()),
        send: vi.fn((): Promise<void> => Promise.resolve()),
        on: vi.fn(),
        off: vi.fn()
    }
    return { hub, connect: vi.fn((): Promise<typeof hub> => Promise.resolve(hub)) }
})

vi.mock('@/cells/app/composables/useSignalR', () => ({
    useSignalR: () => ({ state: { hubs: {} }, connect: signalrMocks.connect })
}))

// ── fixtures ──────────────────────────────────────────────────────
const makeRequest = (overrides: Partial<ITranslationRequest> = {}): ITranslationRequest =>
    ({
        id: 1,
        jobId: 'job-1',
        title: 'request',
        sourceLanguage: 'EN',
        targetLanguage: 'ZH',
        mediaType: 'Movie',
        source: 'Library',
        status: TRANSLATION_STATUS.PENDING,
        progress: 0,
        ...overrides
    }) as unknown as ITranslationRequest

const queuePage: IPagedResult<ITranslationRequest> = {
    items: [
        makeRequest({
            id: 1,
            title: 'Foundation S01E01',
            status: TRANSLATION_STATUS.INPROGRESS,
            progress: 40,
            startedAt: '2026-10-13T08:00:00Z'
        }),
        makeRequest({ id: 2, title: 'Dune', status: TRANSLATION_STATUS.PENDING }),
        makeRequest({
            id: 3,
            title: 'Silo',
            status: TRANSLATION_STATUS.FAILED,
            progress: 30,
            errorMessage: 'provider timeout',
            retryCount: 1
        }),
        makeRequest({
            id: 4,
            title: 'Severance',
            status: TRANSLATION_STATUS.COMPLETED,
            progress: 100,
            translatedSubtitle: '/out/severance.zh.srt',
            completedAt: '2026-10-12T10:00:00Z'
        }),
        makeRequest({ id: 5, title: 'Fallout', status: TRANSLATION_STATUS.PAUSED, progress: 60 })
    ],
    totalCount: 5,
    pageNumber: 1,
    pageSize: 20
}

// Keys mirror the server contract (the TranslationStatus enum names);
// the queued tab reads the 'Pending' key, not 'Queued'.
const initialStatusCounts: Record<string, number> = {
    InProgress: 2,
    Pending: 12,
    Failed: 1,
    Completed: 9
}

// ── scaffold ──────────────────────────────────────────────────────
// flushPromises relies on setImmediate/setTimeout which the fake
// timers own — advancing by 0ms yields the event loop the same way
// and additionally drains the microtask queue.
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

interface MountedQueue {
    wrapper: VueWrapper
    store: ReturnType<typeof useTranslationRequestStore>
    uiStore: ReturnType<typeof useUiStore>
    router: ReturnType<typeof createRouter>
}

async function mountQueuePage(): Promise<MountedQueue> {
    const pinia = createPinia()

    // the real English copy is loaded so tab labels are asserted against
    // what users actually see, not just against the i18n keys
    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false,
        warnHtmlMessage: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/queue', name: 'queue', component: QueuePage },
            { path: '/library', name: 'library', component: { template: '<div />' } }
        ]
    })
    await router.push('/queue')
    await router.isReady()

    const wrapper = mount(QueuePage, {
        global: { plugins: [pinia, i18n, router] },
        shallow: true
    })
    await settle()

    return {
        wrapper,
        store: useTranslationRequestStore(pinia),
        uiStore: useUiStore(pinia),
        router
    }
}

// ── tab bar helpers ───────────────────────────────────────────────
const tabButtons = (wrapper: VueWrapper): DOMWrapper<Element>[] =>
    wrapper.findAll('div.mb-2 button')

// a tab renders as "label count" once the template whitespace collapses
const tabText = (tab: DOMWrapper<Element>): string =>
    tab
        .text()
        .split(/\s+/)
        .filter(Boolean)
        .join(' ')

describe('QueuePage', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        requestsMock.mockResolvedValue(queuePage)
        statusCountsMock.mockResolvedValue(initialStatusCounts)
        proofreadStatusMock.mockResolvedValue({ supported: true })
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('renders the five status tabs with service counts and highlights "All" on mount', async () => {
        const { wrapper } = await mountQueuePage()
        const tabs = tabButtons(wrapper)
        const labels = en.translations.queueTabs

        expect(tabs).toHaveLength(5)
        // "All" sums every status count; each status tab reads its own key
        expect(tabs.map(tabText)).toEqual([
            `${labels.all} 24`,
            `${labels.inProgress} 2`,
            `${labels.pending} 12`,
            `${labels.failed} 1`,
            `${labels.completed} 9`
        ])

        expect(tabs[0].classes()).toContain('border-accent')
        tabs.slice(1).forEach((tab) => expect(tab.classes()).not.toContain('border-accent'))

        // the fetched page rendered: every fixture row is on screen
        const expectedTitles = ['Foundation S01E01', 'Dune', 'Silo', 'Severance', 'Fallout']
        expectedTitles.forEach((title) => expect(wrapper.text()).toContain(title))

        // first page fetched with the default filter (no status)
        expect(requestsMock).toHaveBeenCalledTimes(1)
        expect(requestsMock.mock.calls[0]).toEqual([1, '', 'CreatedAt', true, null])

        // mount wiring: proofread capability probe + live progress hub
        expect(proofreadStatusMock).toHaveBeenCalledTimes(1)
        expect(signalrMocks.connect).toHaveBeenCalledWith(
            'TranslationRequests',
            '/signalr/TranslationRequests'
        )
        expect(signalrMocks.hub.on).toHaveBeenCalledWith('RequestProgress', expect.anything())
    })

    it('refetches page 1 filtered by status when a tab is clicked and moves the highlight', async () => {
        const { wrapper, store } = await mountQueuePage()
        store.filter.pageNumber = 3 // the user had paged before switching tabs

        const failedTab = tabButtons(wrapper)[3]
        expect(tabText(failedTab)).toBe(`${en.translations.queueTabs.failed} 1`)

        await failedTab.trigger('click')
        await settle()

        expect(requestsMock).toHaveBeenCalledTimes(2)
        expect(requestsMock.mock.calls[1]).toEqual([1, '', 'CreatedAt', true, 'Failed'])

        const tabs = tabButtons(wrapper)
        expect(tabs[3].classes()).toContain('border-accent')
        expect(tabs[0].classes()).not.toContain('border-accent')
    })

    it('updates the tab badges when a refetch returns new counts', async () => {
        const { wrapper, store } = await mountQueuePage()
        const labels = en.translations.queueTabs

        expect(tabButtons(wrapper).map(tabText)).toEqual([
            `${labels.all} 24`,
            `${labels.inProgress} 2`,
            `${labels.pending} 12`,
            `${labels.failed} 1`,
            `${labels.completed} 9`
        ])

        statusCountsMock.mockResolvedValue({ InProgress: 0, Pending: 3, Failed: 5, Completed: 1 })
        await store.fetch()
        await settle()

        expect(tabButtons(wrapper).map(tabText)).toEqual([
            `${labels.all} 9`,
            `${labels.inProgress} 0`,
            `${labels.pending} 3`,
            `${labels.failed} 5`,
            `${labels.completed} 1`
        ])
    })

    it('registers the new-task CTA, routes to the library on click and clears it on unmount', async () => {
        const { wrapper, uiStore, router } = await mountQueuePage()

        expect(uiStore.cta).not.toBeNull()
        expect(uiStore.cta?.label).toBe(en.topbar.cta.newTask)
        expect(uiStore.subOverride).not.toBe('')

        await uiStore.cta?.handler()
        expect(router.currentRoute.value.name).toBe('library')

        wrapper.unmount()
        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
        expect(signalrMocks.hub.off).toHaveBeenCalledWith('RequestProgress', expect.anything())
    })
})
