// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import QueueDetailPage from '@/cells/queue/pages/QueueDetailPage.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import useTranslationRequestStore from '@/cells/app/stores/translationRequest'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'
import {
    ITranslationRequestDetail,
    ITranslationRequestEvent,
    TRANSLATION_EVENT_TYPE,
    TRANSLATION_STATUS
} from '@/types'

const translationRequestApi = vi.hoisted(() => ({
    get: vi.fn(),
    getEvents: vi.fn(),
    pause: vi.fn(),
    resumePaused: vi.fn(),
    convert: vi.fn(),
    // store.pause()/resumePaused() refetch the list after the action
    requests: vi.fn(),
    statusCounts: vi.fn()
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => translationRequestApi
}))

const translateApi = vi.hoisted(() => ({
    proofreadStatus: vi.fn()
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => translateApi
}))

// the page joins the live hub on mount; a real connection would negotiate
// over the network, so the composable is replaced wholesale
vi.mock('@/cells/queue/composables/useTranslationDetailHub', () => ({
    useTranslationDetailHub: () => ({ connection: null, connect: vi.fn() })
}))

// ── fixtures ──────────────────────────────────────────────────────
const makeEvent = (
    eventType: string | null,
    overrides: Partial<ITranslationRequestEvent> = {}
): ITranslationRequestEvent =>
    ({
        id: Math.random(),
        status: TRANSLATION_STATUS.INPROGRESS,
        eventType,
        provider: null,
        message: null,
        createdAt: '2026-10-13T15:35:00Z',
        ...overrides
    }) as ITranslationRequestEvent

const makeDetail = (
    overrides: Partial<ITranslationRequestDetail> = {}
): ITranslationRequestDetail =>
    ({
        id: 2417,
        jobId: 'job-2417',
        title: 'Shōgun S01E05',
        sourceLanguage: 'EN',
        targetLanguage: 'ZH',
        mediaType: 'Episode',
        status: TRANSLATION_STATUS.INPROGRESS,
        source: 'Library',
        progress: 0,
        lines: [
            { position: 1, source: 'The Shōgun commands.', target: '将军下令。', service: 'openai' },
            { position: 2, source: 'Sail west.', target: '向西航行。', service: 'openai' }
        ],
        events: [],
        ...overrides
    }) as unknown as ITranslationRequestDetail

const batchEvents = (): ITranslationRequestEvent[] => [
    // ten minutes ago, so the elapsed stat is derivable under fake timers
    makeEvent(TRANSLATION_EVENT_TYPE.STARTED, {
        createdAt: new Date(Date.now() - 10 * 60_000).toISOString()
    }),
    makeEvent(TRANSLATION_EVENT_TYPE.BATCH_COMPLETED, {
        message: 'batch 2/4 · 256/512 lines'
    })
]

// ── scaffold ──────────────────────────────────────────────────────
// The pause/resume chain crosses several awaited store actions, so a
// single timer tick is not enough — drain the microtask queue in rounds.
const settle = async (): Promise<void> => {
    for (let i = 0; i < 5; i++) {
        await vi.advanceTimersByTimeAsync(0)
    }
}

interface MountedDetail {
    wrapper: VueWrapper
    store: ReturnType<typeof useTranslationRequestStore>
    uiStore: ReturnType<typeof useUiStore>
    toastStore: ReturnType<typeof useToastStore>
    router: ReturnType<typeof createRouter>
}

async function mountDetail(detail: ITranslationRequestDetail): Promise<MountedDetail> {
    translationRequestApi.get.mockResolvedValue(detail)
    translationRequestApi.getEvents.mockResolvedValue(batchEvents())
    translationRequestApi.requests.mockResolvedValue({
        items: [],
        totalCount: 0,
        pageNumber: 1,
        pageSize: 20
    })
    translationRequestApi.statusCounts.mockResolvedValue({})
    translateApi.proofreadStatus.mockResolvedValue({ supported: false })

    const pinia = createPinia()
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
            { path: '/queue/:id', name: 'queue-detail', component: QueueDetailPage, props: true },
            { path: '/queue', name: 'queue', component: { template: '<div />' } },
            { path: '/logs', name: 'logs', component: { template: '<div />' } },
            { path: '/proofread/:id?', name: 'proofread', component: { template: '<div />' } }
        ]
    })
    await router.push('/queue/2417')
    await router.isReady()

    const wrapper = mount(QueueDetailPage, {
        props: { id: '2417' },
        global: {
            plugins: [pinia, i18n, router],
            stubs: {
                // heavy line-level panel: not part of this spec
                LineProofreadPanel: true
            }
        }
    })
    await settle()
    await settle()

    return {
        wrapper,
        store: useTranslationRequestStore(pinia),
        uiStore: useUiStore(pinia),
        toastStore: useToastStore(pinia),
        router
    }
}

const pauseButton = (wrapper: VueWrapper) => wrapper.find('[data-test="detail-pause"]')

describe('QueueDetailPage (drill-down, prototype screen-09)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('roots on the shared DrillDownPage skeleton (dd-page)', async () => {
        const { wrapper } = await mountDetail(makeDetail())

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount point
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
    })

    it('renders the pb-head: back to queue, mono sub line from detail fields, logs action', async () => {
        const { wrapper } = await mountDetail(makeDetail())

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(en.nav.queue)
        const sub = wrapper.find('.mono.text-secondary-content')
        expect(sub.text()).toContain('#2417')
        expect(sub.text()).toContain('EN → ZH')
        expect(sub.text()).toContain('2')
        expect(wrapper.find('[data-test="detail-logs"]').text()).toBe(
            en.translations.detail.logs
        )
    })

    it('navigates back to the queue through the pb-head back button', async () => {
        const { wrapper, router } = await mountDetail(makeDetail())

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('queue')
    })

    it('fills the stats grid from real detail/event data', async () => {
        const { wrapper } = await mountDetail(makeDetail())

        const stats = wrapper.find('[data-test="qd-stats"]')
        expect(stats.exists()).toBe(true)
        const labels = stats.findAll('.text-\\[11\\.5px\\]').map((cell) => cell.text())
        expect(labels).toContain(en.translations.detail.statTranslated)
        expect(labels).toContain(en.translations.detail.statCurrentBatch)
        expect(labels).toContain(en.translations.detail.statElapsed)
        // cost never appears: no event message carries an amount
        expect(labels).not.toContain(en.translations.detail.statCost)
        // remaining needs live progress (0 on a fresh load) -> absent
        expect(labels).not.toContain(en.translations.detail.statRemaining)

        // translated cell: 2 persisted lines
        expect(stats.text()).toContain('2')
        // current batch: 2 of 4 completed -> batch 3/4 in progress
        expect(stats.text()).toContain('3/4')
    })

    it('hides the pause action and topbar CTA for a completed request', async () => {
        const { wrapper, uiStore } = await mountDetail(
            makeDetail({ status: TRANSLATION_STATUS.COMPLETED, completedAt: '2026-10-13T16:00:00Z' })
        )

        expect(pauseButton(wrapper).exists()).toBe(false)
        expect(uiStore.cta).toBeNull()
    })

    // ── pause / resume wiring (P0: pb-head button = topbar CTA) ────
    it('pauses through the pb-head primary action and swaps both buttons to resume', async () => {
        translationRequestApi.pause.mockResolvedValue(undefined)
        const { wrapper, uiStore, toastStore } = await mountDetail(makeDetail())

        expect(pauseButton(wrapper).text()).toContain('⏸')
        expect(uiStore.cta?.label).toBe(en.translations.detail.pauseAction)

        await pauseButton(wrapper).trigger('click')
        await settle()

        expect(translationRequestApi.pause).toHaveBeenCalledTimes(1)
        expect(pauseButton(wrapper).text()).toContain('▶')
        expect(uiStore.cta?.label).toBe(en.translations.detail.resumeAction)
        expect(toastStore.toasts[0].message).toBe(
            en.translations.detail.pauseToast.replace('{id}', '2417')
        )
    })

    it('resumes a paused request and swaps both buttons back to pause', async () => {
        translationRequestApi.resumePaused.mockResolvedValue(undefined)
        const { wrapper, uiStore, toastStore } = await mountDetail(
            makeDetail({ status: TRANSLATION_STATUS.PAUSED })
        )

        expect(pauseButton(wrapper).text()).toContain('▶')
        expect(uiStore.cta?.label).toBe(en.translations.detail.resumeAction)

        await pauseButton(wrapper).trigger('click')
        await settle()

        expect(translationRequestApi.resumePaused).toHaveBeenCalledTimes(1)
        expect(pauseButton(wrapper).text()).toContain('⏸')
        expect(uiStore.cta?.label).toBe(en.translations.detail.pauseAction)
        expect(toastStore.toasts[0].message).toBe(
            en.translations.detail.resumeToast.replace('{id}', '2417')
        )
    })

    it('drives the same state machine from the topbar CTA handler', async () => {
        translationRequestApi.pause.mockResolvedValue(undefined)
        const { wrapper, uiStore } = await mountDetail(makeDetail())

        await uiStore.cta?.handler()
        await settle()

        expect(translationRequestApi.pause).toHaveBeenCalledTimes(1)
        expect(pauseButton(wrapper).text()).toContain('▶')
    })

    it('clears the CTA and sub override on unmount', async () => {
        const { wrapper, uiStore } = await mountDetail(makeDetail())

        wrapper.unmount()

        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
    })
})
