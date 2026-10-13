// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import services from '@/services'
import LibraryPage from '@/pages/LibraryPage.vue'
import { useSettingStore } from '@/store/setting'
import { useToastStore } from '@/store/toast'
import { useUiStore } from '@/store/ui'
import { IMediaLibraryItem, IPagedResult, ITranslationEstimate, MEDIA_TYPE, SETTINGS } from '@/types'

vi.mock('@/services', () => ({
    default: {
        media: { library: vi.fn(), estimate: vi.fn() },
        statistics: { getPipelineStatistics: vi.fn() },
        translationRequest: { statusCounts: vi.fn() }
    }
}))

const libraryMock = vi.mocked(services.media.library)
const estimateMock = vi.mocked(services.media.estimate)
const statisticsMock = vi.mocked(services.statistics.getPipelineStatistics)
const statusCountsMock = vi.mocked(services.translationRequest.statusCounts)

// ── fixtures (partial rows: only the fields the page reads) ──────
const show1 = {
    id: 1,
    mediaType: MEDIA_TYPE.SHOW,
    title: 'Foundation',
    tracks: [],
    state: 'None',
    seasonCount: 2,
    episodeCount: 20,
    completedCount: 0,
    failedCount: 0
} as unknown as IMediaLibraryItem

const movie2 = {
    id: 2,
    mediaType: MEDIA_TYPE.MOVIE,
    title: 'Dune',
    tracks: [],
    state: 'None',
    seasonCount: 0,
    episodeCount: 1,
    completedCount: 0,
    failedCount: 0
} as unknown as IMediaLibraryItem

const show3 = {
    id: 3,
    mediaType: MEDIA_TYPE.SHOW,
    title: 'Severance',
    tracks: [],
    state: 'None',
    seasonCount: 1,
    episodeCount: 10,
    completedCount: 0,
    failedCount: 0
} as unknown as IMediaLibraryItem

const libraryPage: IPagedResult<IMediaLibraryItem> = {
    items: [show1, movie2, show3],
    totalCount: 3,
    pageNumber: 1,
    pageSize: 20
}

libraryMock.mockResolvedValue(libraryPage)
estimateMock.mockResolvedValue({ characters: 1200, provider: 'openai', cost: 0.02 } as ITranslationEstimate)
statisticsMock.mockResolvedValue({
    mediaCount: 10,
    missingSubtitles: 63,
    queueDepth: 1,
    servicesOnline: 2,
    writtenThisMonth: 42,
    successRate30Days: null,
    monthlyCost: null
})
statusCountsMock.mockResolvedValue({ InProgress: 2 })

// ── scaffold ─────────────────────────────────────────────────────
// The real ContextMenu is replaced so the tray's imperative open()
// can be observed; a render function is used instead of a template
// string because the runtime-only Vue build cannot compile them.
const trayOpen = vi.fn()
const ContextMenuStub = defineComponent({
    setup(_, { expose }) {
        expose({ open: trayOpen })
    },
    render() {
        return h('div')
    }
})

// flushPromises relies on setImmediate/setTimeout which the fake
// timers own — advancing by 0ms yields the event loop the same way
// and additionally drains the microtask queue.
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

interface LibraryVm {
    items: IMediaLibraryItem[]
    selection: Map<string, IMediaLibraryItem>
    estimate: ITranslationEstimate | null
    missingCount: number
    inProgressCount: number
    toggleSelect: (item: IMediaLibraryItem) => void
    toggleSelectAll: () => void
    clearSelection: () => void
}

interface MountedLibrary {
    wrapper: VueWrapper
    vm: LibraryVm
    uiStore: ReturnType<typeof useUiStore>
    toastStore: ReturnType<typeof useToastStore>
    router: ReturnType<typeof createRouter>
}

async function mountLibraryPage(): Promise<MountedLibrary> {
    const pinia = createPinia()

    // the page root is v-if="configured" — the settings gate must be
    // satisfied before mount, and target_languages feeds the estimate
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SONARR_SETTINGS_COMPLETED, 'true')
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, [{ code: 'ZH', name: 'Chinese' }])

    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en: {} },
        missingWarn: false,
        fallbackWarn: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/library', name: 'library', component: LibraryPage },
            { path: '/import', name: 'import', component: { template: '<div />' } }
        ]
    })
    await router.push('/library')
    await router.isReady()

    const wrapper = mount(LibraryPage, {
        global: {
            plugins: [pinia, i18n, router],
            stubs: { ContextMenu: ContextMenuStub }
        },
        shallow: true
    })
    await settle()

    return {
        wrapper,
        vm: wrapper.vm as unknown as LibraryVm,
        uiStore: useUiStore(pinia),
        toastStore: useToastStore(pinia),
        router
    }
}

describe('LibraryPage', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('fetches the first page with default filters on mount', async () => {
        const { wrapper, vm } = await mountLibraryPage()

        expect(libraryMock).toHaveBeenCalledTimes(1)
        expect(libraryMock).toHaveBeenCalledWith({
            pageNumber: 1,
            searchQuery: '',
            sortBy: 'Title',
            ascending: true,
            mediaType: null,
            state: null,
            targetLanguage: null
        })

        // data landed: 3 rows in the matrix, toolbar stats populated
        expect(wrapper.findAll('tbody tr')).toHaveLength(3)
        expect(vm.missingCount).toBe(63)
        expect(vm.inProgressCount).toBe(2)
    })

    it('writes the type segment to the route query and refetches', async () => {
        const { wrapper, router } = await mountLibraryPage()

        const tabs = wrapper.find('[role="group"]').findAll('button')
        expect(tabs).toHaveLength(3)

        await tabs[1].trigger('click')
        await settle()

        expect(router.currentRoute.value.query.type).toBe('show')
        expect(libraryMock).toHaveBeenCalledTimes(2)
        expect(libraryMock).toHaveBeenLastCalledWith({
            pageNumber: 1,
            searchQuery: '',
            sortBy: 'Title',
            ascending: true,
            mediaType: 'Show',
            state: null,
            targetLanguage: null
        })
    })

    it('debounces the search input by 300ms', async () => {
        const { wrapper } = await mountLibraryPage()

        await wrapper.find('input[type="search"]').setValue('dune')
        expect(libraryMock).toHaveBeenCalledTimes(1)

        await vi.advanceTimersByTimeAsync(300)
        expect(libraryMock).toHaveBeenCalledTimes(2)
        expect(libraryMock).toHaveBeenLastCalledWith(
            expect.objectContaining({ searchQuery: 'dune', pageNumber: 1 })
        )
    })

    it('estimates the selection cost after 400ms and clears with the selection', async () => {
        const { vm } = await mountLibraryPage()

        vm.toggleSelect(show1)
        await vi.advanceTimersByTimeAsync(400)

        expect(estimateMock).toHaveBeenCalledTimes(1)
        expect(estimateMock).toHaveBeenCalledWith([{ mediaType: 'Show', id: 1 }], 'ZH')
        expect(vm.estimate).not.toBeNull()

        vm.clearSelection()
        expect(vm.estimate).toBeNull()

        await vi.advanceTimersByTimeAsync(400)
        expect(estimateMock).toHaveBeenCalledTimes(1)
    })

    it('prunes selections of the media type the active tab no longer shows', async () => {
        const { wrapper, vm } = await mountLibraryPage()

        vm.toggleSelect(movie2)
        vm.toggleSelect(show1)
        expect(vm.selection.size).toBe(2)

        await wrapper.find('[role="group"]').findAll('button')[1].trigger('click')
        await settle()

        expect(vm.selection.has('Movie:2')).toBe(false)
        expect(vm.selection.has('Show:1')).toBe(true)
    })

    it('toggles selection of all visible items in one click', async () => {
        const { vm } = await mountLibraryPage()
        expect(vm.items).toHaveLength(3)

        vm.toggleSelectAll()
        expect(vm.selection.size).toBe(3)

        vm.toggleSelectAll()
        expect(vm.selection.size).toBe(0)
    })

    it('registers the topbar CTA: hint on empty selection, tray menu once something is selected', async () => {
        const { vm, uiStore, toastStore } = await mountLibraryPage()

        expect(uiStore.cta).not.toBeNull()

        uiStore.cta?.handler()
        expect(toastStore.toasts).toHaveLength(1)
        expect(toastStore.toasts[0].type).toBe('info')
        expect(trayOpen).not.toHaveBeenCalled()

        vm.toggleSelect(show1)
        await settle() // tray renders and binds the ContextMenu template ref

        uiStore.cta?.handler()
        expect(trayOpen).toHaveBeenCalledTimes(1)
    })

    it('clears the topbar CTA and sub override on unmount', async () => {
        const { wrapper, uiStore } = await mountLibraryPage()

        expect(uiStore.cta).not.toBeNull()
        expect(uiStore.subOverride).not.toBe('')

        wrapper.unmount()

        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
    })
})
