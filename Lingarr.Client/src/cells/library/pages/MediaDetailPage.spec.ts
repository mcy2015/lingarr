// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import MediaDetailPage from '@/cells/library/pages/MediaDetailPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { useUiStore } from '@/cells/app/stores/ui'
import en from '@/i18n/locales/en.json'
import {
    IMediaLibraryItem,
    IPagedResult,
    ISubtitle,
    ITranslationRequest,
    SETTINGS,
    TRANSLATION_STATUS
} from '@/types'

const libraryMock = vi.hoisted(() => vi.fn())
const requestsMock = vi.hoisted(() => vi.fn())
const collectMock = vi.hoisted(() => vi.fn())
const translateSubtitleMock = vi.hoisted(() => vi.fn())
const getActiveTranslationsMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({ library: libraryMock })
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => ({
        requests: requestsMock,
        getActiveTranslations: getActiveTranslationsMock
    })
}))

vi.mock('@/cells/library/services/subtitleService', () => ({
    subtitleService: () => ({ collect: collectMock })
}))

// the translate store builds its API at module scope; stubbing the service
// module keeps translateSubtitle observable without a network
vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        translateSubtitle: translateSubtitleMock,
        getLanguages: vi.fn(),
        bulkTranslate: vi.fn()
    })
}))

// ── fixtures ──────────────────────────────────────────────────────
const emptyPage = <T,>(): IPagedResult<T> => ({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 })

const movieItem = {
    id: 7,
    mediaType: 'Movie',
    title: 'Dune: Part Two',
    seasonCount: 0,
    episodeCount: 1,
    tracks: [
        { language: 'en', have: 1, total: 1, state: 'complete' },
        { language: 'zh', have: 0, total: 1, state: 'none' }
    ],
    state: 'None',
    completedCount: 0,
    failedCount: 0,
    subtitleFiles: [
        {
            path: '/media/movies/dune.en.srt',
            language: 'en',
            fileName: 'dune',
            format: '.en.srt',
            caption: ''
        }
    ]
} as unknown as IMediaLibraryItem

const showItem = {
    id: 3,
    mediaType: 'Show',
    title: 'Shōgun',
    seasonCount: 1,
    episodeCount: 3,
    tracks: [
        { language: 'en', have: 3, total: 3, state: 'complete' },
        { language: 'zh', have: 1, total: 3, state: 'partial' }
    ],
    state: 'InProgress',
    completedCount: 1,
    failedCount: 0,
    show: {
        id: 3,
        sonarrId: 1,
        title: 'Shōgun',
        path: '/shows/shogun',
        images: [],
        seasons: [
            {
                id: 11,
                seasonNumber: 1,
                path: '/shows/shogun/s01',
                showId: 3,
                episodes: [
                    { id: 21, episodeNumber: 1, title: 'Anjin', fileName: 'shogun.s01e01.mkv' },
                    { id: 22, episodeNumber: 2, title: 'Servant', fileName: 'shogun.s01e02.mkv' },
                    { id: 23, episodeNumber: 3, title: 'Tomorrow', fileName: 'shogun.s01e03.mkv' }
                ]
            }
        ]
    }
} as unknown as IMediaLibraryItem

// ep01 has an EN + ZH file (complete), ep02/ep03 only EN (missing ZH)
const seasonFiles: ISubtitle[] = [
    { path: '/shows/shogun/s01/a.en.srt', fileName: 'shogun.s01e01', language: 'en', format: '.srt', caption: '' },
    { path: '/shows/shogun/s01/a.zh.srt', fileName: 'shogun.s01e01', language: 'zh', format: '.srt', caption: '' },
    { path: '/shows/shogun/s01/b.en.srt', fileName: 'shogun.s01e02', language: 'en', format: '.srt', caption: '' },
    { path: '/shows/shogun/s01/c.en.srt', fileName: 'shogun.s01e03', language: 'en', format: '.srt', caption: '' }
]

const makeRequest = (overrides: Partial<ITranslationRequest>): ITranslationRequest =>
    ({
        id: 1,
        jobId: 'job-1',
        title: 'request',
        sourceLanguage: 'en',
        targetLanguage: 'zh',
        mediaType: 'Movie',
        source: 'Library',
        progress: 0,
        ...overrides
    }) as unknown as ITranslationRequest

const busyRequest = makeRequest({
    id: 2418,
    title: 'Dune: Part Two',
    status: TRANSLATION_STATUS.INPROGRESS,
    progress: 38,
    serviceType: 'deepl',
    mediaId: 7,
    startedAt: new Date(Date.now() - 30 * 60_000).toISOString()
})

// the real ContextMenu is stubbed so tests never depend on its menu flow
const ContextMenuStub = defineComponent({
    emits: ['select'],
    setup(_, { slots }) {
        return () => h('div', { class: 'context-menu-stub' }, slots.default?.())
    }
})

const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

interface DetailVm {
    movieTranslateMissing: () => Promise<void>
    translateMissing: (language: { code: string }) => Promise<void>
    activeRequests: ITranslationRequest[]
}

interface MountedDetail {
    wrapper: VueWrapper
    vm: DetailVm
    uiStore: ReturnType<typeof useUiStore>
    toastStore: ReturnType<typeof useToastStore>
    router: ReturnType<typeof createRouter>
}

async function mountDetail(
    type: 'show' | 'movie',
    routePath: string
): Promise<MountedDetail> {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SOURCE_LANGUAGES, [{ code: 'EN', name: 'English' }])
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, [{ code: 'ZH', name: 'Chinese' }])

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
            { path: '/library', name: 'library', component: { template: '<div />' } },
            { path: '/import', name: 'import', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } },
            { path: '/queue/:id', name: 'queue-detail', component: { template: '<div />' }, props: true },
            { path: '/proofread/:id?', name: 'proofread', component: { template: '<div />' } },
            {
                path: '/library/show/:id',
                name: 'media-detail-show',
                component: MediaDetailPage,
                props: { type: 'show' }
            },
            {
                path: '/library/movie/:id',
                name: 'media-detail-movie',
                component: MediaDetailPage,
                props: { type: 'movie' }
            }
        ]
    })
    await router.push(routePath)
    await router.isReady()

    // route props above are static; the real router uses a props fn — set it
    // through the mounted component's props instead by re-pushing with props
    const wrapper = mount(MediaDetailPage, {
        props: { type, id: type === 'movie' ? '7' : '3' },
        global: {
            plugins: [pinia, i18n, router],
            stubs: { ContextMenu: ContextMenuStub }
        }
    })
    await settle()

    return {
        wrapper,
        vm: wrapper.vm as unknown as DetailVm,
        uiStore: useUiStore(pinia),
        toastStore: useToastStore(pinia),
        router
    }
}

describe('MediaDetailPage · 电影详情(screen-08)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({ items: [movieItem], totalCount: 1, pageNumber: 1, pageSize: 20 })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 55 })
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('renders the pb-head with the back link, language pair and import action', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.find('[data-test="pb-back"]').text()).toBe(en.mediaDetail.back)
        expect(wrapper.text()).toContain('Dune: Part Two')
        // mono sub: movie type + derived pair from the coverage scan
        expect(wrapper.find('div.mono').text()).toContain('movie · EN → ZH')
        expect(wrapper.text()).toContain(en.library.toolbar.import)
        // chips row replaces the removed subtitle-files card (EN ok / ZH none)
        expect(wrapper.text()).toContain('EN ✓')
        expect(wrapper.text()).toContain('ZH ·')
        // no per-file subtitle list renders anywhere on the movie screen
        expect(wrapper.findAll('table')).toHaveLength(0)
    })

    it('shows the untranslated empty state when no job is running and creates the translation on CTA', async () => {
        const { wrapper, vm, toastStore, uiStore } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.text()).toContain(en.mediaDetail.movie.untranslated)
        const start = wrapper.findAll('button').find((b) => b.text() === en.mediaDetail.movie.start)
        expect(start).toBeDefined()

        // the topbar CTA shares the screen action (same creation path)
        await uiStore.cta!.handler()
        await settle()

        expect(translateSubtitleMock).toHaveBeenCalledTimes(1)
        expect(translateSubtitleMock).toHaveBeenCalledWith(
            7,
            movieItem.subtitleFiles![0],
            'en',
            expect.objectContaining({ code: 'ZH' }),
            'Movie'
        )
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.movie.queuedToast.replace('{id}', '55')
        )
        expect(vm.activeRequests).toHaveLength(0) // refreshed history still empty
    })

    it('renders the in-progress task card from the real request and toasts busy on CTA', async () => {
        requestsMock.mockResolvedValue({ items: [busyRequest], totalCount: 1, pageNumber: 1, pageSize: 20 })
        const { wrapper, uiStore, toastStore } = await mountDetail('movie', '/library/movie/7')

        // shared JobRow renders with the request's real data
        expect(wrapper.find('[data-test="jobrow-2418"]').exists()).toBe(true)
        expect(wrapper.text()).toContain('38%')
        // card meta: request id + service
        expect(wrapper.text()).toContain('request #2418 · deepl')
        // target chip switches to the busy variant with live progress
        expect(wrapper.text()).toContain('ZH ⏳ 38%')
        // untranslated empty state is gone while a job runs
        expect(wrapper.text()).not.toContain(en.mediaDetail.movie.untranslated)

        await uiStore.cta!.handler()
        await settle()

        expect(translateSubtitleMock).not.toHaveBeenCalled()
        expect(toastStore.toasts).toHaveLength(1)
        expect(toastStore.toasts[0].message).toBe('Translation in progress · request #2418 · 38%')
    })

    it('keeps the history card as a request list linking to queue-detail', async () => {
        const done = makeRequest({
            id: 9,
            title: 'Dune: Part Two',
            status: TRANSLATION_STATUS.COMPLETED,
            mediaId: 7,
            serviceType: 'deepl'
        })
        requestsMock.mockResolvedValue({ items: [done], totalCount: 1, pageNumber: 1, pageSize: 20 })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.text()).toContain(en.mediaDetail.history)
        const links = wrapper.findAll('a').filter((a) => a.attributes('href') === '/queue/9')
        expect(links).toHaveLength(1)
    })
})

describe('MediaDetailPage · 剧集详情(screen-07)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({ items: [showItem], totalCount: 1, pageNumber: 1, pageSize: 20 })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        collectMock.mockResolvedValue(seasonFiles)
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 77 })
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('appends the computed missing-episode count to the head sub line', async () => {
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // 3 episodes, 1 ZH track → 2 missing
        const sub = wrapper.find('div.mono').text()
        expect(sub).toContain(en.mediaDetail.showMeta.replace('{seasons}', '1').replace('{episodes}', '3'))
        expect(sub).toContain('2 episodes missing ZH subtitles')
    })

    it('queues every missing episode through the shared CTA, toasts the real count and jumps to the queue', async () => {
        const { uiStore, toastStore, router } = await mountDetail('show', '/library/show/3')

        expect(uiStore.cta!.label).toBe(en.mediaDetail.translateMissing)
        await uiStore.cta!.handler()
        await settle()

        // ep02 + ep03 lack ZH; ep01 is complete
        expect(translateSubtitleMock).toHaveBeenCalledTimes(2)
        expect(translateSubtitleMock).toHaveBeenCalledWith(
            22,
            expect.objectContaining({ language: 'en' }),
            'en',
            expect.objectContaining({ code: 'ZH' }),
            'Episode'
        )
        expect(toastStore.toasts[0].message).toBe('Queued 2 episodes · EN → ZH')
        expect(toastStore.toasts[0].type).toBe('success')
        expect(router.currentRoute.value.name).toBe('queue')
    })

    it('toasts "nothing to queue" when every episode already has the target track', async () => {
        collectMock.mockResolvedValue([
            ...seasonFiles,
            { path: '/shows/shogun/s01/b.zh.srt', fileName: 'shogun.s01e02', language: 'zh', format: '.srt', caption: '' },
            { path: '/shows/shogun/s01/c.zh.srt', fileName: 'shogun.s01e03', language: 'zh', format: '.srt', caption: '' }
        ])
        const { uiStore, toastStore, router } = await mountDetail('show', '/library/show/3')

        await uiStore.cta!.handler()
        await settle()

        expect(translateSubtitleMock).not.toHaveBeenCalled()
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.show.noneQueued)
        expect(router.currentRoute.value.name).toBe('media-detail-show')
    })

    it('links the episode detail button to the episode request when one is in flight or failed', async () => {
        requestsMock.mockResolvedValue({
            items: [
                makeRequest({ id: 30, mediaId: 22, status: TRANSLATION_STATUS.INPROGRESS, progress: 72 }),
                makeRequest({ id: 31, mediaId: 23, status: TRANSLATION_STATUS.FAILED, errorMessage: '429' })
            ],
            totalCount: 2,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // scoped to the episode table — the history card links the same ids
        const detailLinks = wrapper.find('table').findAll('a')
        expect(detailLinks.map((a) => a.attributes('href')).sort()).toEqual(['/queue/30', '/queue/31'])
    })

    it('hides the episode detail button when the episode has no request id', async () => {
        requestsMock.mockResolvedValue({
            items: [makeRequest({ id: 30, mediaId: 22, status: TRANSLATION_STATUS.INPROGRESS })],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // only ep02 has a request; ep01/ep03 must not render a detail link
        const detailLinks = wrapper.find('table').findAll('a')
        expect(detailLinks.map((a) => a.attributes('href'))).toEqual(['/queue/30'])
    })
})
