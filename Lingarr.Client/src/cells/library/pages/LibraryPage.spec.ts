// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import LibraryPage from '@/cells/library/pages/LibraryPage.vue'
import PaginationComponent from '@/cells/kernel/components/PaginationComponent.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { useUiStore } from '@/cells/app/stores/ui'
import { IMediaLibraryItem, IPagedResult, ITranslationEstimate, MEDIA_TYPE, SETTINGS } from '@/types'

const statisticsMock = vi.hoisted(() => vi.fn())
const statusCountsMock = vi.hoisted(() => vi.fn())
const libraryMock = vi.hoisted(() => vi.fn())
const estimateMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({ library: libraryMock, estimate: estimateMock })
}))

vi.mock('@/cells/app/api/statisticsService', () => ({
    statisticsService: () => ({ getPipelineStatistics: statisticsMock })
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => ({ statusCounts: statusCountsMock })
}))

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

async function mountLibraryPage(path = '/library'): Promise<MountedLibrary> {
    const pinia = createPinia()

    // the page root is v-if="configured" — the settings gate must be
    // satisfied before mount, and target_languages feeds the estimate
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SONARR_SETTINGS_COMPLETED, 'true')
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, [{ code: 'ZH', name: 'Chinese' }])

    // messages cover only the keys the row title link asserts; everything
    // else renders as its key and is not asserted anywhere
    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: {
            en: {
                library: {
                    table: { openDetail: 'Open {type} {title} details' },
                    badge: { show: 'Show', movie: 'Movie' }
                }
            }
        },
        missingWarn: false,
        fallbackWarn: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/library', name: 'library', component: LibraryPage },
            { path: '/import', name: 'import', component: { template: '<div />' } },
            // detail drill-downs the title links and the Details button target
            { path: '/library/show/:id', name: 'media-detail-show', component: { template: '<div />' }, props: true },
            { path: '/library/movie/:id', name: 'media-detail-movie', component: { template: '<div />' }, props: true }
        ]
    })
    await router.push(path)
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

    // ── overview deep link (screen-01「缺字幕检测」node) ─────────────
    it('activates the missing-subtitles view from the ?missing=1 deep link', async () => {
        await mountLibraryPage('/library?missing=1')

        // expressed through the existing target-language filter: items whose
        // primary-target (ZH) track is not yet complete
        expect(libraryMock).toHaveBeenCalledTimes(1)
        expect(libraryMock).toHaveBeenLastCalledWith({
            pageNumber: 1,
            searchQuery: '',
            sortBy: 'Title',
            ascending: true,
            mediaType: null,
            state: null,
            targetLanguage: 'ZH'
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

    it('writes pagination updates back in place and refetches the new page', async () => {
        // only rendered past the first page — fake a larger library
        libraryMock.mockResolvedValueOnce({ ...libraryPage, totalCount: 60 })
        const { wrapper } = await mountLibraryPage()
        expect(libraryMock).toHaveBeenCalledTimes(1)

        const pagination = wrapper.findComponent(PaginationComponent)
        expect(pagination.exists()).toBe(true)

        pagination.vm.$emit('update:modelValue', {
            pageNumber: 2,
            searchQuery: '',
            sortBy: 'Title',
            isAscending: true
        })
        await settle()

        expect(libraryMock).toHaveBeenCalledTimes(2)
        expect(libraryMock).toHaveBeenLastCalledWith(
            expect.objectContaining({ pageNumber: 2 })
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

    // ── requirement card C: title click → detail ─────────────────────
    it('navigates to the show detail route when a show row title is clicked', async () => {
        const { wrapper, router } = await mountLibraryPage()

        const title = wrapper.findAll('tbody a')[0] // show1「Foundation」
        expect(title.attributes('href')).toBe('/library/show/1')
        expect(title.attributes('aria-label')).toBe('Open Show Foundation details')

        await title.trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('media-detail-show')
        expect(router.currentRoute.value.path).toBe('/library/show/1')
    })

    it('navigates to the movie detail route when a movie row title is clicked', async () => {
        const { wrapper, router } = await mountLibraryPage()

        const title = wrapper.findAll('tbody a')[1] // movie2「Dune」
        expect(title.attributes('href')).toBe('/library/movie/2')

        await title.trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('media-detail-movie')
        expect(router.currentRoute.value.path).toBe('/library/movie/2')
    })

    it('leaves modified title clicks to the browser so open-in-new-tab works', async () => {
        const { wrapper, router } = await mountLibraryPage()

        const title = wrapper.findAll('tbody a')[1]
        // listener attached after Vue's own handler: observes whether the
        // component preventDefault()ed the modified click (it must not)
        let defaultPrevented = false
        title.element.addEventListener('click', (event) => {
            defaultPrevented = event.defaultPrevented
        })

        await title.trigger('click', { metaKey: true })
        await settle()

        expect(defaultPrevented).toBe(false)
        expect(router.currentRoute.value.path).toBe('/library')
    })

    it('toggles selection without navigating when a row checkbox is used', async () => {
        const { wrapper, router, vm } = await mountLibraryPage()

        // movie2 row checkbox — selection changes, route stays on the list
        await wrapper.findAll('tbody input[type="checkbox"]')[1].trigger('change')
        await settle()

        expect(vm.selection.has('Movie:2')).toBe(true)
        expect(router.currentRoute.value.path).toBe('/library')
    })

    it('still routes the existing Details button to the detail page', async () => {
        const { wrapper, router } = await mountLibraryPage()

        // row action cell: [import, details]
        const detail = wrapper.findAll('tbody tr')[0].findAll('button')[1]
        await detail.trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('media-detail-show')
        expect(router.currentRoute.value.path).toBe('/library/show/1')
    })
})
