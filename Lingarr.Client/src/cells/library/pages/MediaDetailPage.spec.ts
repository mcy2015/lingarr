// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import MediaDetailPage from '@/cells/library/pages/MediaDetailPage.vue'
import SubtitleSearchModal from '@/cells/library/components/SubtitleSearchModal.vue'
import SubtitleToolsModal from '@/cells/library/components/SubtitleToolsModal.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
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
const translateEmbeddedTrackMock = vi.hoisted(() => vi.fn())
const getActiveTranslationsMock = vi.hoisted(() => vi.fn())
const mediaSubtitlesMock = vi.hoisted(() => vi.fn())
const subtitleNeedMock = vi.hoisted(() => vi.fn())
const setTargetLanguagesMock = vi.hoisted(() => vi.fn())
const extractEmbeddedTrackMock = vi.hoisted(() => vi.fn())
const mergeBilingualMock = vi.hoisted(() => vi.fn())

/// mediaSubtitles payload shape (F2a + requirement card A/B): external
/// files + embedded tracks + the container's audio languages (null=unknown).
const subtitlesPayload = (
    overrides: Partial<{
        files: unknown[]
        embedded: unknown[]
        embeddedScanned: boolean
        audioLanguages: string[] | null
    }> = {}
) => ({
    files: [],
    embedded: [],
    embeddedScanned: false,
    audioLanguages: null,
    ...overrides
})

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({
        library: libraryMock,
        mediaSubtitles: mediaSubtitlesMock,
        subtitleNeed: subtitleNeedMock,
        setTargetLanguages: setTargetLanguagesMock,
        extractEmbeddedTrack: extractEmbeddedTrackMock
    })
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => ({
        requests: requestsMock,
        getActiveTranslations: getActiveTranslationsMock
    })
}))

vi.mock('@/cells/library/services/subtitleService', () => ({
    subtitleService: () => ({ collect: collectMock, mergeBilingual: mergeBilingualMock })
}))

// the translate store builds its API at module scope; stubbing the service
// module keeps translateSubtitle observable without a network
vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        translateSubtitle: translateSubtitleMock,
        translateEmbeddedTrack: translateEmbeddedTrackMock,
        getLanguages: vi.fn(),
        bulkTranslate: vi.fn()
    })
}))

// ── fixtures ──────────────────────────────────────────────────────
const emptyPage = <T>(): IPagedResult<T> => ({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 20
})

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
    {
        path: '/shows/shogun/s01/a.en.srt',
        fileName: 'shogun.s01e01',
        language: 'en',
        format: '.srt',
        caption: ''
    },
    {
        path: '/shows/shogun/s01/a.zh.srt',
        fileName: 'shogun.s01e01',
        language: 'zh',
        format: '.srt',
        caption: ''
    },
    {
        path: '/shows/shogun/s01/b.en.srt',
        fileName: 'shogun.s01e02',
        language: 'en',
        format: '.srt',
        caption: ''
    },
    {
        path: '/shows/shogun/s01/c.en.srt',
        fileName: 'shogun.s01e03',
        language: 'en',
        format: '.srt',
        caption: ''
    }
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
    routePath: string,
    targets: { code: string; name: string }[] = [{ code: 'ZH', name: 'Chinese' }]
): Promise<MountedDetail> {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SOURCE_LANGUAGES, [{ code: 'EN', name: 'English' }])
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, targets)

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
            {
                path: '/queue/:id',
                name: 'queue-detail',
                component: { template: '<div />' },
                props: true
            },
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
            // the modal is stubbed so entry tests assert the page→modal
            // contract (mediaType/mediaId props) without network fan-out
            stubs: { ContextMenu: ContextMenuStub, SubtitleSearchModal: true }
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
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 55 })
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 91 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('roots on the shared DrillDownPage skeleton (dd-page)', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount point
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
    })

    it('scrolls the whole column instead of clipping it below the viewport', async () => {
        // the episode table + subtitle files + history cards exceed one
        // 1080p viewport; an overflow-hidden page made everything below the
        // fold unreachable (user-visible: subtitle file rows impossible to
        // see) — the column must scroll
        const { wrapper } = await mountDetail('show', '/library/show/7')

        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.classes()).toContain('overflow-y-auto')

        // the subtitle files card no longer caps at 240px with a ~100px
        // inner scroll port: its rows live on the page scroll
        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.classes().join(' ')).not.toContain('max-h-[240px]')
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
        // C5: none-state chip renders the bare code + a styled dot ELEMENT —
        // never a trailing text '·' that reads like a dangling separator
        const chips = wrapper.findAll('[data-test="track-chip"]')
        expect(chips[1].text()).toBe('ZH')
        expect(chips[1].find('[data-test="track-none-dot"]').exists()).toBe(true)
        expect(wrapper.text()).not.toContain('ZH ·')
        // no per-file subtitle list renders anywhere on the movie screen
        expect(wrapper.findAll('table')).toHaveLength(0)
    })

    it('marks the forced dimension inside the same language chip (B07)', async () => {
        // zh carries ONLY a forced track: the chip keeps its honest none
        // state (dot stays — the full subtitle is still missing) and gains
        // one forced mark inside the same chip; no second badge row exists.
        libraryMock.mockResolvedValue({
            items: [
                {
                    ...movieItem,
                    tracks: [
                        { language: 'en', have: 1, total: 1, state: 'complete' },
                        { language: 'zh', have: 0, total: 1, state: 'none', forcedHave: 1 }
                    ]
                }
            ],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const chips = wrapper.findAll('[data-test="track-chip"]')
        const forcedMark = chips[1].find('[data-test="track-forced-mark"]')
        expect(forcedMark.exists()).toBe(true)
        expect(forcedMark.text()).toBe(en.library.track.forcedTag)
        // forced-only never flips the full-coverage verdict: the none dot
        // stays and the chip stays in its dimmed none styling
        expect(chips[1].find('[data-test="track-none-dot"]').exists()).toBe(true)
        expect(chips[1].text()).toContain('ZH')
        // a complete track without forced files renders no mark (EN)
        expect(chips[0].find('[data-test="track-forced-mark"]').exists()).toBe(false)
    })

    it('renders no forced mark for pre-B07 payloads (field absent)', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.findAll('[data-test="track-forced-mark"]')).toHaveLength(0)
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

    it('keeps the topbar CTA as the movie translate entry — no duplicated head button', async () => {
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem, imdbId: 'tt2229499' }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper, uiStore } = await mountDetail('movie', '/library/movie/7')

        // R1: the head actions keep their real entries (IMDb / import /
        // subtitle search), but the「translate missing」button is gone — it
        // duplicated the topbar CTA label and handler one-for-one
        expect(wrapper.find('[data-test="imdb-link"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="open-subtitle-search"]').exists()).toBe(true)
        expect(wrapper.text()).toContain(en.library.toolbar.import)
        expect(
            wrapper.findAll('button').find((b) => b.text() === en.mediaDetail.translateMissing)
        ).toBeUndefined()
        // the CTA stays registered under the plain label (no season wording
        // on a movie) and keeps the busy/create handler
        expect(uiStore.cta!.label).toEqual({ key: 'mediaDetail.translateMissing' })
    })

    // ═══ C2:全目标语言就绪 → 静默完成态(无 Start translation)═══
    it('shows the silent all-done state instead of the start CTA when every target track is complete', async () => {
        libraryMock.mockResolvedValue({
            items: [
                {
                    ...movieItem,
                    tracks: [
                        { language: 'en', have: 1, total: 1, state: 'complete' },
                        { language: 'zh', have: 1, total: 1, state: 'complete' }
                    ]
                }
            ],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.find('[data-test="tasks-all-done"]').text()).toContain(
            en.mediaDetail.tasks.allDone
        )
        expect(wrapper.text()).not.toContain(en.mediaDetail.movie.untranslated)
        expect(
            wrapper.findAll('button').find((b) => b.text() === en.mediaDetail.movie.start)
        ).toBeUndefined()
    })

    it('keeps the per-target done state while any target language track is still missing', async () => {
        // targets [ZH, JA]: first target ZH complete, JA missing → the
        // all-done state must NOT claim completion (only every() unlocks it)
        libraryMock.mockResolvedValue({
            items: [
                {
                    ...movieItem,
                    tracks: [
                        { language: 'en', have: 1, total: 1, state: 'complete' },
                        { language: 'zh', have: 1, total: 1, state: 'complete' },
                        { language: 'ja', have: 0, total: 1, state: 'none' }
                    ]
                }
            ],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7', [
            { code: 'ZH', name: 'Chinese' },
            { code: 'JA', name: 'Japanese' }
        ])

        expect(wrapper.find('[data-test="tasks-all-done"]').exists()).toBe(false)
        // the established first-target done message still covers the gap
        expect(wrapper.text()).toContain(en.mediaDetail.movie.done)
    })

    it('renders the in-progress task card from the real request and toasts busy on CTA', async () => {
        requestsMock.mockResolvedValue({
            items: [busyRequest],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
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
        requestsMock.mockResolvedValue({
            items: [done],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.text()).toContain(en.mediaDetail.history)
        const links = wrapper.findAll('a').filter((a) => a.attributes('href') === '/queue/9')
        expect(links).toHaveLength(1)
    })

    it('keeps the single media id filter for movie histories', async () => {
        await mountDetail('movie', '/library/movie/7')

        // a movie is its own media: the legacy single-id filter still applies
        expect(requestsMock).toHaveBeenCalledTimes(1)
        expect(requestsMock).toHaveBeenCalledWith(1, '', 'CreatedAt', false, null, '7')
    })
})

describe('MediaDetailPage · 剧集详情(screen-07)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [showItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        collectMock.mockResolvedValue(seasonFiles)
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 77 })
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 92 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('appends the computed missing-episode count to the head sub line', async () => {
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // 3 episodes, 1 ZH track → 2 missing; the head line joins the split
        // plural keys (1 → one form, 3 → other form) as "1 season · 3 episodes"
        const sub = wrapper.find('div.mono').text()
        const seasonPart = en.mediaDetail.seasonCount
            .split('|')[0]
            .trim()
            .replace('{n}', '1')
        const episodePart = en.mediaDetail.episodeCount
            .split('|')[1]
            .trim()
            .replace('{n}', '3')
        expect(sub).toContain(`${seasonPart} · ${episodePart}`)
        expect(sub).toContain('2 episodes missing ZH subtitles')
    })

    it('queues every missing episode through the shared CTA, toasts the real count and jumps to the queue', async () => {
        const { uiStore, toastStore, router } = await mountDetail('show', '/library/show/3')

        expect(uiStore.cta!.label).toEqual({ key: 'mediaDetail.ctaTranslateSeason' })
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

    it('labels the show CTA「season」while the in-page button keeps the language-menu label', async () => {
        const { wrapper, uiStore } = await mountDetail('show', '/library/show/3')

        // R2: the topbar CTA runs the whole season for the first target
        // without the language menu — its label must say so and differ from
        // the in-page button (which still opens the menu)
        expect(uiStore.cta!.label).toEqual({ key: 'mediaDetail.ctaTranslateSeason' })
        const screenButton = wrapper
            .findAll('button')
            .find((b) => b.text() === en.mediaDetail.translateMissing)
        expect(screenButton).toBeDefined()
        expect(en.mediaDetail.ctaTranslateSeason).toContain('season')
        expect(en.mediaDetail.ctaTranslateSeason).not.toBe(en.mediaDetail.translateMissing)
    })

    it('toasts "nothing to queue" when every episode already has the target track', async () => {
        collectMock.mockResolvedValue([
            ...seasonFiles,
            {
                path: '/shows/shogun/s01/b.zh.srt',
                fileName: 'shogun.s01e02',
                language: 'zh',
                format: '.srt',
                caption: ''
            },
            {
                path: '/shows/shogun/s01/c.zh.srt',
                fileName: 'shogun.s01e03',
                language: 'zh',
                format: '.srt',
                caption: ''
            }
        ])
        const { uiStore, toastStore, router } = await mountDetail('show', '/library/show/3')

        await uiStore.cta!.handler()
        await settle()

        expect(translateSubtitleMock).not.toHaveBeenCalled()
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.show.noneQueued)
        expect(router.currentRoute.value.name).toBe('media-detail-show')
    })

    it('scopes the episode history to the parent show instead of an episode id list', async () => {
        await mountDetail('show', '/library/show/3')

        // P1-6: the show's episode requests roll up server-side by the parent
        // show id — no comma-joined episode id list travels in the URL
        expect(requestsMock).toHaveBeenCalledTimes(1)
        expect(requestsMock).toHaveBeenCalledWith(1, '', 'CreatedAt', false, null, null, 3)
        expect(String(requestsMock.mock.calls[0][5] ?? '')).not.toContain('21,22,23')
    })

    it('renders thousand-episode seasons through the virtualized table (viewport rows only)', async () => {
        const episodes = Array.from({ length: 1000 }, (_, i) => ({
            id: 1000 + i,
            episodeNumber: i + 1,
            title: `Episode ${i + 1}`,
            fileName: `show.s01e${String(i + 1).padStart(4, '0')}.mkv`
        }))
        const bigShowItem = {
            ...showItem,
            episodeCount: 1000,
            show: {
                ...showItem.show!,
                seasons: [{ ...showItem.show!.seasons[0], episodes }]
            }
        } as unknown as IMediaLibraryItem
        libraryMock.mockResolvedValue({
            items: [bigShowItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })

        const { wrapper } = await mountDetail('show', '/library/show/3')

        // C4: the episode card no longer stretches to fill the viewport
        // (flex-[1.5] removed) — it self-sizes under a scroll cap so short
        // seasons leave no dead space while long ones still scroll inside
        const card = wrapper.find('[data-test="episode-list-card"]')
        expect(card.exists()).toBe(true)
        expect(card.classes().some((cls) => cls === 'flex-[1.5]')).toBe(false)
        expect(card.classes().some((cls) => cls.startsWith('max-h-['))).toBe(true)

        // content rows only: the virtual spacers carry data-test markers and
        // the empty-state row carries the noEpisodes text
        const dataRows = () =>
            wrapper
                .find('table')
                .findAll('tbody tr')
                .filter(
                    (row) =>
                        !row.attributes('data-test') && row.text() !== en.mediaDetail.noEpisodes
                )
        // a thousand-episode season renders only the initial viewport window
        // (fallback viewport 480px / 40px row estimate + ±5 overscan)
        expect(dataRows().length).toBeLessThan(40)
        expect(dataRows()[0].text()).toContain('E01')

        // scrolling moves the window deep into the season — still bounded
        const scroller = wrapper.find('[data-test="virtual-scroll-container"]')
        scroller.element.scrollTop = 40 * 500
        await scroller.trigger('scroll')
        const deepRows = dataRows()
        expect(deepRows.length).toBeLessThan(40)
        expect(deepRows[0].text()).toContain('E496')

        // the client-side pager is gone with the virtualization — the whole
        // season lives in one scrollable list
        expect(wrapper.find('[data-test="episode-pager"]').exists()).toBe(false)
    })

    it('links the episode detail button to the episode request when one is in flight or failed', async () => {
        requestsMock.mockResolvedValue({
            items: [
                makeRequest({
                    id: 30,
                    mediaId: 22,
                    status: TRANSLATION_STATUS.INPROGRESS,
                    progress: 72
                }),
                makeRequest({
                    id: 31,
                    mediaId: 23,
                    status: TRANSLATION_STATUS.FAILED,
                    errorMessage: '429'
                })
            ],
            totalCount: 2,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // scoped to the episode table — the history card links the same ids
        const detailLinks = wrapper.find('table').findAll('a')
        expect(detailLinks.map((a) => a.attributes('href')).sort()).toEqual([
            '/queue/30',
            '/queue/31'
        ])
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

    // ═══ C3:Proofread 按钮短标签,悬浮/读屏保留整句 ═══
    it('labels the episode proofread entry with the short tag and keeps the full sentence on hover', async () => {
        requestsMock.mockResolvedValue({
            items: [makeRequest({ id: 40, mediaId: 21, status: TRANSLATION_STATUS.COMPLETED })],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        const button = wrapper.find('[data-test="episode-proofread"]')
        expect(button.exists()).toBe(true)
        expect(button.text()).toBe(en.common.translationAction.proofreadShort)
        expect(button.attributes('title')).toBe(en.common.translationAction.proofread)
        expect(button.attributes('aria-label')).toBe(en.common.translationAction.proofread)
        // the row links into the proofread screen with the request id
        expect(button.element.closest('a')?.getAttribute('href')).toBe('/proofread/40')
    })

    // ═══ 字幕文件卡:目录实际存在的文件 + 双语标签 ═══
    it('lists every subtitle file with per-language chips, dual files carrying both', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                files: [
                    {
                        fileName: '[DBD-Raws][Movie][FLAC].scjp.ass',
                        directory: '/movies/Movie(2023){tmdb-1}',
                        languages: ['zh', 'ja'],
                        format: 'ass',
                        bytes: 4096
                    },
                    {
                        fileName: 'Movie.english.srt',
                        directory: '/movies/Movie(2023){tmdb-1}',
                        languages: ['en'],
                        format: 'srt',
                        bytes: 1024
                    }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.exists()).toBe(true)
        const rows = card.findAll('[data-test="subtitle-file-row"]')
        expect(rows).toHaveLength(2)

        const dual = rows[0]
        expect(dual.text()).toContain('.scjp.ass')
        // 双语文件:简体中文 + 日语两枚标签,同一行
        expect(dual.text()).toContain(en.mediaDetail.lang.zh)
        expect(dual.text()).toContain(en.mediaDetail.lang.ja)
        expect(dual.text()).toContain('ass')

        // 计数行
        expect(card.text()).toContain(en.mediaDetail.subtitleFiles.count.replace('{count}', '2'))
    })

    it('shows the empty hint when no external subtitles live next to the media', async () => {
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ embeddedScanned: true }))
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.text()).toContain(en.mediaDetail.subtitleFiles.empty)
    })

    // ═══ 内嵌轨道分区:ffprobe 探测结果(F2a 纯展示)═══
    it('lists embedded tracks with language chips and the graphic marker on PGS', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                embedded: [
                    {
                        mediaId: 7,
                        streamIndex: 2,
                        language: 'zh',
                        rawLanguage: 'chi',
                        title: '简体中文',
                        codec: 'srt',
                        isTextBased: true
                    },
                    {
                        mediaId: 7,
                        streamIndex: 3,
                        language: null,
                        rawLanguage: 'und',
                        title: null,
                        codec: 'pgs',
                        isTextBased: false
                    }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.find('[data-test="embedded-tracks-section"]').exists()).toBe(true)
        const rows = card.findAll('[data-test="embedded-track-row"]')
        expect(rows).toHaveLength(2)

        // 文本轨:规范语言 chip + 容器标题 + codec,无图形标注
        const text = rows[0]
        expect(text.text()).toContain('#2')
        expect(text.text()).toContain('简体中文')
        expect(text.text()).toContain(en.mediaDetail.lang.zh)
        expect(text.text()).toContain('srt')
        expect(text.text()).not.toContain(en.mediaDetail.subtitleFiles.embedded.graphic)

        // 图形轨:识别不出 → 灰 chip 显示原始 tag,标「图形 · 不可翻译」
        const graphic = rows[1]
        expect(graphic.text()).toContain('und')
        expect(graphic.text()).toContain(en.mediaDetail.subtitleFiles.embedded.graphic)
        expect(graphic.text()).toContain('pgs')

        // 分区计数行
        expect(card.text()).toContain(
            en.mediaDetail.subtitleFiles.embedded.count.replace('{count}', '2')
        )
    })

    it('shows the actionable not-scanned hint before the first probe', async () => {
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.find('[data-test="embedded-not-scanned"]').exists()).toBe(true)
        expect(card.text()).toContain(en.mediaDetail.subtitleFiles.embedded.notScanned)
        expect(card.findAll('[data-test="embedded-track-row"]')).toHaveLength(0)
    })

    it('shows the scanned-empty hint when the container has no subtitle streams', async () => {
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ embeddedScanned: true }))
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        expect(card.find('[data-test="embedded-empty"]').exists()).toBe(true)
        expect(card.text()).toContain(en.mediaDetail.subtitleFiles.embedded.empty)
        expect(card.find('[data-test="embedded-not-scanned"]').exists()).toBe(false)
    })
})

// ═══ F2b:内嵌文本轨翻译入口(电影)════════════════════════════
describe('MediaDetailPage · 内嵌轨道翻译(F2b · 电影)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 91 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    const embeddedPayload = (embedded: unknown[], files: unknown[] = []) =>
        subtitlesPayload({
            files,
            embedded,
            embeddedScanned: true
        })

    it('renders the translate entry only on text tracks with a recognized language and uncovered target', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            embeddedPayload([
                {
                    mediaId: 7,
                    streamIndex: 2,
                    language: 'ja',
                    rawLanguage: 'jpn',
                    title: '日本語',
                    codec: 'ass',
                    isTextBased: true
                },
                {
                    mediaId: 7,
                    streamIndex: 3,
                    language: null,
                    rawLanguage: 'xyzzy',
                    title: null,
                    codec: 'srt',
                    isTextBased: true
                },
                {
                    mediaId: 7,
                    streamIndex: 4,
                    language: 'zh-TW',
                    rawLanguage: 'zh-TW',
                    title: null,
                    codec: 'pgs',
                    isTextBased: false
                }
            ])
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const rows = card.findAll('[data-test="embedded-track-row"]')
        expect(rows).toHaveLength(3)

        // text + recognized + ZH uncovered → entry
        expect(rows[0].find('[data-test="embedded-translate"]').exists()).toBe(true)
        // language unknown → no entry
        expect(rows[1].find('[data-test="embedded-translate"]').exists()).toBe(false)
        // graphic → no entry
        expect(rows[2].find('[data-test="embedded-translate"]').exists()).toBe(false)
    })

    it('hides every entry when the target language is already covered (external or embedded)', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            embeddedPayload(
                [
                    {
                        mediaId: 7,
                        streamIndex: 2,
                        language: 'ja',
                        rawLanguage: 'jpn',
                        title: null,
                        codec: 'srt',
                        isTextBased: true
                    }
                ],
                // an external ZH file already covers the only target language
                [
                    {
                        fileName: 'dune.zh.srt',
                        directory: '/movies/dune',
                        languages: ['zh'],
                        format: 'srt',
                        bytes: 512
                    }
                ]
            )
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.findAll('[data-test="embedded-translate"]')).toHaveLength(0)
    })

    it('queues the embedded-track translation on menu select and toasts the request id', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            embeddedPayload([
                {
                    mediaId: 7,
                    streamIndex: 2,
                    language: 'ja',
                    rawLanguage: 'jpn',
                    title: '日本語',
                    codec: 'ass',
                    isTextBased: true
                }
            ])
        )
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="embedded-track-row"]')
        const menu = row.findComponent(ContextMenuStub)
        expect(menu.exists()).toBe(true)

        menu.vm.$emit('select', { code: 'ZH', name: 'Chinese' })
        await settle()

        expect(translateEmbeddedTrackMock).toHaveBeenCalledTimes(1)
        expect(translateEmbeddedTrackMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            streamIndex: 2,
            sourceLanguage: 'ja',
            targetLanguage: 'ZH'
        })
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.queuedToast.replace('{id}', '91')
        )
        expect(toastStore.toasts[0].type).toBe('success')
    })

    it('toasts the readable server error when initiation is rejected', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            embeddedPayload([
                {
                    mediaId: 7,
                    streamIndex: 2,
                    language: 'ja',
                    rawLanguage: 'jpn',
                    title: null,
                    codec: 'srt',
                    isTextBased: true
                }
            ])
        )
        translateEmbeddedTrackMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: "'Dune: Part Two' already has zh subtitles" } }
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const menu = wrapper.find('[data-test="embedded-track-row"]').findComponent(ContextMenuStub)
        menu.vm.$emit('select', { code: 'ZH', name: 'Chinese' })
        await settle()

        expect(translateEmbeddedTrackMock).toHaveBeenCalledTimes(1)
        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.queuedFailed.replace(
                '{message}',
                "'Dune: Part Two' already has zh subtitles"
            )
        )
    })
})

// ═══ F2b:内嵌文本轨翻译入口(剧集,按集归属)══════════════════
describe('MediaDetailPage · 内嵌轨道翻译(F2b · 剧集)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [showItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        collectMock.mockResolvedValue(seasonFiles)
        getActiveTranslationsMock.mockResolvedValue([])
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 92 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('scopes coverage per episode and queues with the owning episode id', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                // ep01 already has a ZH external file; ep02 does not
                files: [
                    {
                        fileName: 'shogun.s01e01.zh.srt',
                        directory: '/shows/shogun/s01',
                        languages: ['zh'],
                        format: 'srt',
                        bytes: 256
                    }
                ],
                embedded: [
                    {
                        mediaId: 21,
                        streamIndex: 2,
                        language: 'en',
                        rawLanguage: 'eng',
                        title: 'English',
                        codec: 'srt',
                        isTextBased: true
                    },
                    {
                        mediaId: 22,
                        streamIndex: 2,
                        language: 'en',
                        rawLanguage: 'eng',
                        title: 'English',
                        codec: 'srt',
                        isTextBased: true
                    }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper, toastStore } = await mountDetail('show', '/library/show/3')

        const rows = wrapper.findAll('[data-test="embedded-track-row"]')
        expect(rows).toHaveLength(2)

        // ep01's ZH is covered → no entry; ep02's is not → entry
        expect(rows[0].find('[data-test="embedded-translate"]').exists()).toBe(false)
        const menu = rows[1].findComponent(ContextMenuStub)
        expect(menu.exists()).toBe(true)

        menu.vm.$emit('select', { code: 'ZH', name: 'Chinese' })
        await settle()

        expect(translateEmbeddedTrackMock).toHaveBeenCalledWith({
            mediaType: 'Episode',
            mediaId: 22,
            streamIndex: 2,
            sourceLanguage: 'en',
            targetLanguage: 'ZH'
        })
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.queuedToast.replace('{id}', '92')
        )
    })
})

// ═══ 需求卡 B:音轨行 + 「标记需要字幕/取消标记」(电影)════════════
describe('MediaDetailPage · 音轨行与字幕需求标记(需求卡 B · 电影)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        // The mark toggle refreshes the list row in place (by design), so a
        // fresh shallow copy per test keeps the module-level movieItem from
        // carrying a previous test's needsNoSubtitles mutation forward.
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('states the unknown audio honestly and marks the media as needing subtitles', async () => {
        subtitleNeedMock.mockResolvedValue({
            needsNoSubtitles: false,
            needsTranslation: true,
            audioOverride: true
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const row = card.find('[data-test="audio-languages-row"]')
        expect(row.exists()).toBe(true)
        // C1: the audio strip renders as its own bordered/tinted group (an
        // inset coverage box), visually separated from the plain file rows
        expect(row.find('[data-test="audio-coverage-box"]').exists()).toBe(true)
        expect(row.find('[data-test="audio-badge"]').text()).toBe(
            en.mediaDetail.audio.audioLabel
        )
        // null audio renders the honest unknown hint, never fake codes — and
        // no coverage note (nothing is covered)
        expect(row.find('[data-test="audio-languages-value"]').exists()).toBe(false)
        expect(row.find('[data-test="audio-languages-unknown"]').text()).toBe(
            en.mediaDetail.audio.unknown
        )
        expect(row.find('[data-test="audio-coverage-note"]').exists()).toBe(false)

        // unknown audio is conservatively unexempted → the mark entry shows
        const toggle = row.find('[data-test="subtitle-need-toggle"]')
        expect(toggle.text()).toBe(en.mediaDetail.audio.markNeeded)

        await toggle.trigger('click')
        await settle()

        expect(subtitleNeedMock).toHaveBeenCalledTimes(1)
        expect(subtitleNeedMock).toHaveBeenCalledWith('Movie', 7, true)
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.audio.markedToast)
        expect(toastStore.toasts[0].type).toBe('success')
        // in-place refresh: only the subtitle card is re-fetched (no reload)
        expect(mediaSubtitlesMock).toHaveBeenCalledTimes(2)
        // the response's persisted override drives the toggle's new SHORT
        // label (C1); the full sentence moves to the hover title — and the
        // persistent badge makes the manual mark visible on the page
        const after = wrapper.find('[data-test="subtitle-need-toggle"]')
        expect(after.text()).toBe(en.mediaDetail.audio.cancelMarkShort)
        expect(after.attributes('title')).toBe(en.mediaDetail.audio.cancelMark)
        const badge = wrapper.find('[data-test="audio-marked-needed-badge"]')
        expect(badge.exists()).toBe(true)
        expect(badge.text()).toBe(en.mediaDetail.audio.markedBadge)
    })

    it('lists the mono audio codes and offers the override for target-covering audio', async () => {
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem, needsNoSubtitles: true }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ audioLanguages: ['en', 'zh'] }))
        subtitleNeedMock.mockResolvedValue({
            needsNoSubtitles: false,
            needsTranslation: true,
            audioOverride: true
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="audio-languages-row"]')
        expect(row.find('[data-test="audio-languages-value"]').text()).toBe('en · zh')
        expect(row.find('[data-test="audio-languages-unknown"]').exists()).toBe(false)
        // C1: covering audio carries the coverage note with the language list
        expect(row.find('[data-test="audio-coverage-note"]').text()).toContain('en, zh')
        expect(row.find('[data-test="audio-coverage-note"]').text()).toContain(
            'usually no subtitles needed'
        )

        // exempt via zh audio → the entry overrides it back into the pool
        const toggle = row.find('[data-test="subtitle-need-toggle"]')
        expect(toggle.text()).toBe(en.mediaDetail.audio.markNeeded)

        await toggle.trigger('click')
        await settle()

        expect(subtitleNeedMock).toHaveBeenCalledWith('Movie', 7, true)
        expect(toastStore.toasts[0].type).toBe('success')
        // reversible (AC5): with the override active the entry flips to the
        // short clear-mark label (C1) and the badge marks it on the page
        expect(wrapper.find('[data-test="subtitle-need-toggle"]').text()).toBe(
            en.mediaDetail.audio.cancelMarkShort
        )
        expect(wrapper.find('[data-test="audio-marked-needed-badge"]').exists()).toBe(true)
    })

    it('shows the not-needed badge for a persisted mark and clears it back to automatic', async () => {
        // the mark persisted earlier (override=false) must stay VISIBLE —
        // a badge on the page, not just a flipped button — and clearing it
        // sends needed:null so the automatic audio rule applies again
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem, needsNoSubtitles: true, audioOverride: false }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        subtitleNeedMock.mockResolvedValue({
            needsNoSubtitles: true,
            needsTranslation: false,
            audioOverride: null
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="audio-languages-row"]')
        const badge = row.find('[data-test="audio-marked-not-needed-badge"]')
        expect(badge.exists()).toBe(true)
        expect(badge.text()).toBe(en.mediaDetail.audio.notNeededBadge)

        const toggle = row.find('[data-test="subtitle-need-toggle"]')
        expect(toggle.text()).toBe(en.mediaDetail.audio.cancelMarkShort)

        await toggle.trigger('click')
        await settle()

        expect(subtitleNeedMock).toHaveBeenCalledWith('Movie', 7, null)
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.audio.unmarkedToast)
        // mark cleared: the badge disappears with it
        expect(wrapper.find('[data-test="audio-marked-not-needed-badge"]').exists()).toBe(false)
    })

    it('renders no entry when known audio simply lacks the target language', async () => {
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ audioLanguages: ['ja'] }))
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="audio-languages-row"]')
        expect(row.find('[data-test="audio-languages-value"]').text()).toBe('ja')
        // audio without any target → no coverage note, no entry
        expect(row.find('[data-test="audio-coverage-note"]').exists()).toBe(false)
        expect(row.find('[data-test="subtitle-need-toggle"]').exists()).toBe(false)
    })

    it('toasts the readable server error and keeps the state when the mark is rejected', async () => {
        subtitleNeedMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'media not found' } }
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="subtitle-need-toggle"]').trigger('click')
        await settle()

        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.audio.failedToast.replace('{message}', 'media not found')
        )
        // no in-place refresh on failure, label unchanged
        expect(mediaSubtitlesMock).toHaveBeenCalledTimes(1)
        expect(wrapper.find('[data-test="subtitle-need-toggle"]').text()).toBe(
            en.mediaDetail.audio.markNeeded
        )
    })
})

// ═══ QA6:搜索入口(电影头部 / 剧集行内)+ 音轨判定修复 ═══════════
describe('MediaDetailPage · 搜索字幕入口与音轨判定(QA6)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        collectMock.mockResolvedValue(seasonFiles)
        translateSubtitleMock.mockResolvedValue({ jobId: 55 })
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 91 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('opens the movie search from the header with mediaType "movie" and the movie id', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="open-subtitle-search"]').trigger('click')
        await settle()

        const modal = wrapper.findComponent(SubtitleSearchModal)
        expect(modal.exists()).toBe(true)
        // the wire form "movie" is what the backend parser accepts; the old
        // value sent from show pages ("Show") answered 400
        expect(modal.props('mediaType')).toBe('movie')
        expect(modal.props('mediaId')).toBe(7)
        expect(modal.props('title')).toBe('Dune: Part Two')
    })

    it('renders exactly one import action (the v-if="false" duplicate is gone)', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const importButtons = wrapper
            .findAll('button')
            .filter((b) => b.text() === en.library.toolbar.import)
        expect(importButtons).toHaveLength(1)
    })

    it('moves the episode search entry into each episode row with that episode id', async () => {
        libraryMock.mockResolvedValue({
            items: [showItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        // shows are not a searchable unit: no header entry on the show page
        expect(wrapper.find('[data-test="open-subtitle-search"]').exists()).toBe(false)

        // every episode row carries its own search entry
        const buttons = wrapper.findAll('[data-test="episode-subtitle-search"]')
        expect(buttons).toHaveLength(3)

        await buttons[1].trigger('click')
        await settle()

        const modal = wrapper.findComponent(SubtitleSearchModal)
        expect(modal.exists()).toBe(true)
        expect(modal.props('mediaType')).toBe('episode')
        // the second episode's own id, not the show's
        expect(modal.props('mediaId')).toBe(22)
        expect(modal.props('episode')).toBe('S01E02')
        expect(modal.props('title')).toBe('Shōgun')
    })

    it('exempts audio covering ANY configured target language, not just the first', async () => {
        // targets [JA, ZH], audio zh — the first-target-only logic would miss
        // the coverage; the fixed any-target logic must see it
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ audioLanguages: ['zh'] }))
        const { wrapper } = await mountDetail('movie', '/library/movie/7', [
            { code: 'JA', name: 'Japanese' },
            { code: 'ZH', name: 'Chinese' }
        ])

        const row = wrapper.find('[data-test="audio-languages-row"]')
        expect(row.find('[data-test="audio-languages-value"]').text()).toBe('zh')
        // known audio covering a target is auto-exempt; without a manual
        // mark the entry offers to override it BACK into the needing pool —
        // canceling only ever appears once a mark exists
        const toggle = row.find('[data-test="subtitle-need-toggle"]')
        expect(toggle.text()).toBe(en.mediaDetail.audio.markNeeded)
        expect(row.find('[data-test="audio-coverage-note"]').text()).toContain(
            'Audio track already covers zh'
        )
    })

    it('treats an empty audio list as unknown instead of rendering an empty value', async () => {
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload({ audioLanguages: [] }))
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="audio-languages-row"]')
        expect(row.find('[data-test="audio-languages-value"]').exists()).toBe(false)
        expect(row.find('[data-test="audio-languages-unknown"]').exists()).toBe(true)
        // conservative: unknown audio keeps the mark-needed entry
        expect(row.find('[data-test="subtitle-need-toggle"]').text()).toBe(
            en.mediaDetail.audio.markNeeded
        )
    })
})

// ═══ P2 批:缺口数扣豁免 · IMDb 外链 · 搜索弹层的新 props ═══════════
describe('MediaDetailPage · P2 打磨(缺口豁免/IMDb/弹层 props)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 55 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
        collectMock.mockResolvedValue(seasonFiles)
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    const showWithEpisodeFlags = (noSubtitleNeeded: Record<number, boolean>): IMediaLibraryItem =>
        ({
            ...showItem,
            show: {
                ...showItem.show!,
                seasons: [
                    {
                        ...showItem.show!.seasons[0],
                        episodes: showItem.show!.seasons[0].episodes.map((episode) => ({
                            ...episode,
                            noSubtitleNeeded: noSubtitleNeeded[episode.id] ?? false
                        }))
                    }
                ]
            }
        }) as unknown as IMediaLibraryItem

    it('subtracts exempt episodes from the missing-episode count', async () => {
        // 3 episodes, 1 ZH track, ep02+ep03 exempt → nothing missing
        libraryMock.mockResolvedValue({
            items: [showWithEpisodeFlags({ 22: true, 23: true })],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const none = await mountDetail('show', '/library/show/3')
        expect(none.wrapper.find('div.mono').text()).not.toContain('missing ZH subtitles')

        // only ep03 exempt → 3 - 1 exempt - 1 have = 1 still missing
        libraryMock.mockResolvedValue({
            items: [showWithEpisodeFlags({ 23: true })],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const partial = await mountDetail('show', '/library/show/3')
        const sub = partial.wrapper.find('div.mono').text()
        expect(sub).toContain(
            en.mediaDetail.missingEpisodes.replace('{count}', '1').replace('{lang}', 'ZH')
        )
    })

    it('links out to IMDb only when the payload carries an id', async () => {
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem, imdbId: 'tt15239678' }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const linked = await mountDetail('movie', '/library/movie/7')

        const imdb = linked.wrapper.find('[data-test="imdb-link"]')
        expect(imdb.exists()).toBe(true)
        expect(imdb.attributes('href')).toBe('https://www.imdb.com/title/tt15239678/')
        expect(imdb.attributes('target')).toBe('_blank')
        expect(imdb.attributes('aria-label')).toBe(en.mediaDetail.openImdb)

        // legacy rows without the column render no dead link
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const bare = await mountDetail('movie', '/library/movie/7')
        expect(bare.wrapper.find('[data-test="imdb-link"]').exists()).toBe(false)
    })

    it('passes the movie audio and existing subtitle languages to the search modal', async () => {
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                audioLanguages: ['zh'],
                files: [
                    {
                        fileName: 'dune.en.srt',
                        directory: '/movies/dune',
                        languages: ['en'],
                        format: 'srt',
                        bytes: 512
                    }
                ],
                embedded: [
                    {
                        mediaId: 7,
                        streamIndex: 2,
                        language: 'ja',
                        rawLanguage: 'jpn',
                        title: null,
                        codec: 'srt',
                        isTextBased: true
                    }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="open-subtitle-search"]').trigger('click')
        await settle()

        const modal = wrapper.findComponent(SubtitleSearchModal)
        expect(modal.exists()).toBe(true)
        // audio row passthrough drives the modal's native-audio hint
        expect(modal.props('audioLanguages')).toEqual(['zh'])
        // existing = external files + embedded tracks of the searched unit
        expect([...(modal.props('existingLanguages') ?? [])].sort()).toEqual(['en', 'ja'])
    })

    it('scopes the episode search existing languages to that episode', async () => {
        libraryMock.mockResolvedValue({
            items: [showItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        // ep01 has a zh file, ep02 has none — the modal of ep02 must not
        // inherit ep01's languages
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                files: [
                    {
                        fileName: 'shogun.s01e01.zh.srt',
                        directory: '/shows/shogun/s01',
                        languages: ['zh'],
                        format: 'srt',
                        bytes: 256
                    }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('show', '/library/show/3')

        const buttons = wrapper.findAll('[data-test="episode-subtitle-search"]')
        await buttons[1].trigger('click')
        await settle()

        const modal = wrapper.findComponent(SubtitleSearchModal)
        expect(modal.exists()).toBe(true)
        expect(modal.props('mediaId')).toBe(22)
        expect(modal.props('existingLanguages')).toEqual([])
    })
})

// ── 媒体文件批次:海报 + 简介 + 电影文件卡 + 剧集表质量/大小列 ──────
describe('MediaDetailPage · 媒体文件详情(媒体文件批次)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        translateSubtitleMock.mockResolvedValue({ jobId: 55 })
        translateEmbeddedTrackMock.mockResolvedValue({ jobId: 91 })
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    const mediaMovieItem = {
        ...movieItem,
        fileName: 'Dune.Part.Two.2024.2160p.WEB-DL',
        path: '/media/movies/Dune Part Two (2024)',
        quality: 'WEBDL-2160p',
        sizeBytes: 24_841_132_442,
        overview: 'Paul Atreides unites with the Fremen.',
        posterPath: '/MediaCover/7/poster.jpg',
        videoCodec: 'h265',
        audioCodec: 'truehd',
        audioChannels: 7.1,
        resolution: '3840x2160',
        runtimeMinutes: 166,
        dateAdded: '2026-09-01T08:00:00Z'
    } as unknown as IMediaLibraryItem

    it('renders the poster and overview block for a movie carrying both', async () => {
        libraryMock.mockResolvedValue({
            items: [mediaMovieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="media-overview-card"]')
        expect(card.exists()).toBe(true)
        const poster = card.find('[data-test="media-poster"]')
        expect(poster.exists()).toBe(true)
        // poster rides the existing /api/image proxy with the movie segment
        expect(poster.attributes('src')).toBe('/api/image/movie/MediaCover/7/poster.jpg')
        expect(card.find('[data-test="media-overview-text"]').text()).toBe(
            'Paul Atreides unites with the Fremen.'
        )
    })

    it('renders the movie file card with spec chips, size and runtime', async () => {
        libraryMock.mockResolvedValue({
            items: [mediaMovieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="media-file-card"]')
        expect(card.exists()).toBe(true)
        expect(card.find('[data-test="media-file-name"]').text()).toBe(
            'Dune.Part.Two.2024.2160p.WEB-DL'
        )
        expect(card.find('[data-test="media-file-directory"]').text()).toBe(
            '/media/movies/Dune Part Two (2024)'
        )
        // spec chips: quality · derived 2160p · video codec · audio codec + channels
        const chips = card
            .find('[data-test="media-file-specs"]')
            .findAll('span')
            .map((chip) => chip.text())
        expect(chips).toEqual(['WEBDL-2160p', '2160p', 'h265', 'truehd 7.1'])
        // 166 min → "2 h 46 min" (en locale of the spec harness)
        expect(card.find('[data-test="media-file-runtime"]').text()).toContain('2 h 46 min')
        expect(card.find('[data-test="media-file-size"]').text()).toBe('23.1 GB')
    })

    it('omits the overview block entirely when the payload carries neither poster nor overview', async () => {
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        expect(wrapper.find('[data-test="media-overview-card"]').exists()).toBe(false)
    })

    it('renders the show overview through the show image segment', async () => {
        libraryMock.mockResolvedValue({
            items: [
                {
                    ...showItem,
                    overview: 'In feudal Japan, an English sailor becomes a pawn.',
                    posterPath: '/MediaCover/3/poster.jpg'
                }
            ],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        const card = wrapper.find('[data-test="media-overview-card"]')
        expect(card.exists()).toBe(true)
        expect(card.find('[data-test="media-poster"]').attributes('src')).toBe(
            '/api/image/show/MediaCover/3/poster.jpg'
        )
        expect(card.find('[data-test="media-overview-text"]').text()).toContain('feudal Japan')
        // the movie file card never renders on a show page
        expect(wrapper.find('[data-test="media-file-card"]').exists()).toBe(false)
    })

    it('shows per-episode quality and size columns with the spec tooltip', async () => {
        const enrichedShow = {
            ...showItem,
            show: {
                ...showItem.show!,
                seasons: [
                    {
                        ...showItem.show!.seasons[0],
                        episodes: showItem.show!.seasons[0].episodes.map((ep, index) => ({
                            ...ep,
                            quality: index === 0 ? 'WEBDL-1080p' : null,
                            sizeBytes: index === 0 ? 4_600_000_000 : null,
                            videoCodec: 'h264',
                            audioCodec: 'eac3',
                            audioChannels: 5.1,
                            resolution: '1920x1080',
                            runtimeMinutes: 58
                        }))
                    }
                ]
            }
        } as unknown as IMediaLibraryItem
        libraryMock.mockResolvedValue({
            items: [enrichedShow],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        const qualities = wrapper.findAll('[data-test="episode-quality"]')
        const sizes = wrapper.findAll('[data-test="episode-size"]')
        expect(qualities[0].text()).toBe('WEBDL-1080p')
        expect(sizes[0].text()).toBe('4.3 GB')
        // absent facts render the muted dash, not an empty cell
        expect(qualities[1].text()).toBe('—')
        expect(sizes[1].text()).toBe('—')
        // the tooltip carries the full *arr mediaInfo fact set
        expect(qualities[0].attributes('title')).toBe(
            'WEBDL-1080p · 1080p · h264 · eac3 5.1 · 58 min'
        )
    })
})

/// Library mock with a movie item carrying a B08 target-language override.
const movieItemWithOverride = (
    targetLanguagesOverride: string[] | null
): IMediaLibraryItem =>
    ({
        ...movieItem,
        targetLanguagesOverride
    }) as unknown as IMediaLibraryItem

describe('MediaDetailPage · 目标语言集合编辑器(B08 · 电影/剧集双形态)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [{ ...movieItem }],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        mediaSubtitlesMock.mockResolvedValue(subtitlesPayload())
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('follows the global list by default and saves a pinned override', async () => {
        // mountDetail seeds TARGET_LANGUAGES=[ZH] — with no override the row
        // shows that global set and the honest follow-global note
        setTargetLanguagesMock.mockResolvedValue({ languages: ['zh'], source: 'override' })
        // first load: no override; the reload after the write returns the
        // detail projection with the persisted override
        libraryMock
            .mockResolvedValueOnce({
                items: [{ ...movieItem }],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
            .mockResolvedValue({
                items: [movieItemWithOverride(['zh'])],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const row = wrapper.find('[data-test="target-languages-row"]')
        expect(row.exists()).toBe(true)
        expect(row.find('[data-test="target-languages-value"]').text()).toBe('zh')
        expect(row.find('[data-test="target-source-global"]').exists()).toBe(true)
        expect(row.find('[data-test="target-override-badge"]').exists()).toBe(false)
        // movie form carries no episodes-scope note
        expect(row.find('[data-test="target-show-scope"]').exists()).toBe(false)

        await row.find('[data-test="edit-target-languages"]').trigger('click')
        expect(wrapper.find('[data-test="target-editor"]').exists()).toBe(true)
        // the editor opens with NO code preselected (no override stored)
        expect(wrapper.find('[data-test="target-editor-code-zh"]').attributes('aria-pressed')).toBe(
            'false'
        )

        await wrapper.find('[data-test="target-editor-code-zh"]').trigger('click')
        await wrapper.find('[data-test="target-editor-save"]').trigger('click')
        await settle()

        expect(setTargetLanguagesMock).toHaveBeenCalledTimes(1)
        expect(setTargetLanguagesMock).toHaveBeenCalledWith('Movie', 7, ['zh'])
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.b08target.editDone.replace('{langs}', 'ZH')
        )
        expect(toastStore.toasts[0].type).toBe('success')
        // the whole page reloads so the coverage chips re-render under the
        // new effective set (the library row is fetched again)
        expect(libraryMock).toHaveBeenCalledTimes(2)
        // in-place override state update drives the badge immediately
        expect(wrapper.find('[data-test="target-override-badge"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="target-languages-value"]').text()).toBe('zh')
        // the editor closed on success
        expect(wrapper.find('[data-test="target-editor"]').exists()).toBe(false)
    })

    it('show form renders the override badge, the effective set and the episodes scope', async () => {
        // an existing show override [ja] replaces the global [ZH] set — the
        // row shows ONLY ja, never the removed global target
        setTargetLanguagesMock.mockResolvedValue({ languages: ['ja'], source: 'override' })
        libraryMock.mockResolvedValue({
            items: [
                {
                    ...showItem,
                    targetLanguagesOverride: ['ja']
                }
            ],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('show', '/library/show/3')

        const row = wrapper.find('[data-test="target-languages-row"]')
        expect(row.find('[data-test="target-languages-value"]').text()).toBe('ja')
        expect(row.find('[data-test="target-override-badge"]').text()).toBe(
            en.mediaDetail.b08target.overrideBadge
        )
        expect(row.find('[data-test="target-source-global"]').exists()).toBe(false)
        // show form: the series-level override is honest about its scope
        expect(row.find('[data-test="target-show-scope"]').text()).toBe(
            en.mediaDetail.b08target.showScope
        )

        // the editor preselects the stored override codes
        await row.find('[data-test="edit-target-languages"]').trigger('click')
        expect(wrapper.find('[data-test="target-editor-code-ja"]').attributes('aria-pressed')).toBe(
            'true'
        )
        expect(wrapper.find('[data-test="target-editor-code-zh"]').attributes('aria-pressed')).toBe(
            'false'
        )
    })

    it('saves an empty selection as the explicit no-target state and toasts it honestly', async () => {
        setTargetLanguagesMock.mockResolvedValue({ languages: [], source: 'override' })
        libraryMock
            .mockResolvedValueOnce({
                items: [movieItemWithOverride(['zh'])],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
            .mockResolvedValue({
                items: [movieItemWithOverride([])],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="edit-target-languages"]').trigger('click')
        // deselect the preselected zh — saving nothing is the explicit
        //「无目标」state, never a silent reset to global
        await wrapper.find('[data-test="target-editor-code-zh"]').trigger('click')
        await wrapper.find('[data-test="target-editor-save"]').trigger('click')
        await settle()

        expect(setTargetLanguagesMock).toHaveBeenCalledWith('Movie', 7, [])
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.b08target.none)
        expect(toastStore.toasts[0].type).toBe('success')
        // the honest none state renders, the override badge stays (reversible)
        expect(wrapper.find('[data-test="target-languages-none"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="target-languages-value"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="target-override-badge"]').exists()).toBe(true)
    })

    it('reset writes null and returns the media to the global list', async () => {
        setTargetLanguagesMock.mockResolvedValue({ languages: null, source: 'global' })
        libraryMock
            .mockResolvedValueOnce({
                items: [movieItemWithOverride(['zh'])],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
            .mockResolvedValue({
                items: [{ ...movieItem }],
                totalCount: 1,
                pageNumber: 1,
                pageSize: 20
            })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="edit-target-languages"]').trigger('click')
        await wrapper.find('[data-test="target-editor-reset"]').trigger('click')
        await settle()

        expect(setTargetLanguagesMock).toHaveBeenCalledWith('Movie', 7, null)
        expect(toastStore.toasts[0].message).toBe(en.mediaDetail.b08target.resetDone)
        // the badge is gone with the override — the only way out
        expect(wrapper.find('[data-test="target-override-badge"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="target-source-global"]').exists()).toBe(true)
    })

    it('toasts the readable server error and keeps the editor open when the write fails', async () => {
        setTargetLanguagesMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'media not found' } }
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="edit-target-languages"]').trigger('click')
        await wrapper.find('[data-test="target-editor-code-zh"]').trigger('click')
        await wrapper.find('[data-test="target-editor-save"]').trigger('click')
        await settle()

        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.b08target.failedToast.replace('{message}', 'media not found')
        )
        // failure keeps the editor open (nothing was applied) and the page
        // did not reload
        expect(wrapper.find('[data-test="target-editor"]').exists()).toBe(true)
        expect(libraryMock).toHaveBeenCalledTimes(1)
    })
})

// ═══ B04:内嵌轨抽取为外挂(电影行级按钮)══════════════════════
describe('MediaDetailPage · 内嵌轨抽取为外挂(B04)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                embedded: [
                    {
                        mediaId: 7,
                        streamIndex: 2,
                        language: 'zh',
                        rawLanguage: 'chi',
                        title: '简体中文',
                        codec: 'srt',
                        isTextBased: true
                    },
                    {
                        mediaId: 7,
                        streamIndex: 4,
                        language: 'zh',
                        rawLanguage: 'chi',
                        title: null,
                        codec: 'pgs',
                        isTextBased: false
                    }
                ],
                embeddedScanned: true
            })
        )
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('renders the extract button on text tracks only', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const buttons = wrapper.findAll('[data-test="embedded-extract"]')
        expect(buttons).toHaveLength(1) // the graphic row never gets one
        expect(buttons[0].text()).toBe(en.mediaDetail.subtitleFiles.embedded.extract)
    })

    it('extracts through the endpoint and toasts the created sidecar', async () => {
        extractEmbeddedTrackMock.mockResolvedValue({
            path: '/media/movies/dune/dune.zh.srt',
            fileName: 'dune.zh.srt',
            created: true
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="embedded-extract"]').trigger('click')
        await settle()

        expect(extractEmbeddedTrackMock).toHaveBeenCalledTimes(1)
        expect(extractEmbeddedTrackMock).toHaveBeenCalledWith('Movie', 7, 2)
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.extractDone.replace('{file}', 'dune.zh.srt')
        )
        expect(toastStore.toasts[0].type).toBe('success')
        // the page reloaded its payload so the file card reflects the sidecar
        expect(mediaSubtitlesMock.mock.calls.length).toBeGreaterThan(1)
    })

    it('toasts the idempotent answer when the sidecar already existed', async () => {
        extractEmbeddedTrackMock.mockResolvedValue({
            path: '/media/movies/dune/dune.zh.srt',
            fileName: 'dune.zh.srt',
            created: false
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="embedded-extract"]').trigger('click')
        await settle()

        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.extractExists.replace('{file}', 'dune.zh.srt')
        )
        expect(toastStore.toasts[0].type).toBe('success')
    })

    it('toasts the readable server error when extraction is rejected', async () => {
        extractEmbeddedTrackMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'Embedded stream 2 (pgs) is image-based' } }
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        await wrapper.find('[data-test="embedded-extract"]').trigger('click')
        await settle()

        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.subtitleFiles.embedded.extractFailed.replace(
                '{message}',
                'Embedded stream 2 (pgs) is image-based'
            )
        )
    })
})

// ═══ B06:双文件双语合并(行级多选 + 合并条)══════════════════════════
describe('MediaDetailPage · 双语合并(B06)', () => {
    const zhFile = {
        fileName: 'dune.zh.srt',
        directory: '/media/movies',
        languages: ['zh'],
        format: 'srt',
        bytes: 2048
    }
    const enFile = {
        fileName: 'dune.en.srt',
        directory: '/media/movies',
        languages: ['en'],
        format: 'srt',
        bytes: 4096
    }

    const mergeResult = {
        outputPath: '/media/movies/dune.zh.en.srt',
        outputFileName: 'dune.zh.en.srt',
        primaryCues: 100,
        secondaryCues: 100,
        pairedCues: 96,
        primaryOnlyCues: 2,
        secondaryOnlyCues: 2
    }

    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({ files: [zhFile, enFile], embeddedScanned: true })
        )
        mergeBilingualMock.mockResolvedValue(mergeResult)
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('selects two files, merges primary-on-top and refreshes the card', async () => {
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const selects = card.findAll('[data-test="subtitle-file-select"]')
        expect(selects).toHaveLength(2)

        // one pick alone shows no bar
        await selects[0].trigger('click')
        expect(card.find('[data-test="bilingual-merge-bar"]').exists()).toBe(false)

        await selects[1].trigger('click')
        const bar = card.find('[data-test="bilingual-merge-bar"]')
        expect(bar.exists()).toBe(true)
        // first pick = primary (top line)
        expect(bar.find('[data-test="bilingual-merge-primary"]').text()).toContain(
            'dune.zh.srt'
        )
        expect(bar.find('[data-test="bilingual-merge-secondary"]').text()).toContain(
            'dune.en.srt'
        )

        const loadCallsBefore = mediaSubtitlesMock.mock.calls.length
        await bar.find('[data-test="bilingual-merge-action"]').trigger('click')
        await settle()

        expect(mergeBilingualMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            primaryPath: '/media/movies/dune.zh.srt',
            primaryLanguage: 'zh',
            secondaryPath: '/media/movies/dune.en.srt',
            secondaryLanguage: 'en'
        })
        // toast carries the real pairing verdict, then the page reloads the
        // subtitle payload (the merged file shows up in the card)
        expect(toastStore.toasts[0].type).toBe('success')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.b06merge.doneToast
                .replace('{paired}', '96')
                .replace('{count}', '100')
                .replace('{file}', 'dune.zh.en.srt')
        )
        expect(mediaSubtitlesMock.mock.calls.length).toBe(loadCallsBefore + 1)
        // selection cleared after success — the bar is gone
        expect(wrapper.find('[data-test="bilingual-merge-bar"]').exists()).toBe(false)
    })

    it('swap flips which language is the primary (top line)', async () => {
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const selects = card.findAll('[data-test="subtitle-file-select"]')
        await selects[0].trigger('click')
        await selects[1].trigger('click')
        await card.find('[data-test="bilingual-merge-swap"]').trigger('click')

        const bar = card.find('[data-test="bilingual-merge-bar"]')
        expect(bar.find('[data-test="bilingual-merge-primary"]').text()).toContain('dune.en.srt')
        expect(bar.find('[data-test="bilingual-merge-secondary"]').text()).toContain('dune.zh.srt')

        await bar.find('[data-test="bilingual-merge-action"]').trigger('click')
        await settle()

        expect(mergeBilingualMock).toHaveBeenCalledWith(
            expect.objectContaining({
                primaryPath: '/media/movies/dune.en.srt',
                primaryLanguage: 'en',
                secondaryPath: '/media/movies/dune.zh.srt',
                secondaryLanguage: 'zh'
            })
        )
    })

    it('disables the merge when both files carry the same language', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                files: [zhFile, { ...zhFile, fileName: 'dune.chs.srt' }],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const selects = card.findAll('[data-test="subtitle-file-select"]')
        await selects[0].trigger('click')
        await selects[1].trigger('click')

        const action = card.find('[data-test="bilingual-merge-action"]')
        expect(action.attributes('disabled')).toBeDefined()
        expect(action.attributes('title')).toBe(en.mediaDetail.b06merge.sameLanguageTitle)
        expect(mergeBilingualMock).not.toHaveBeenCalled()
    })

    it('keeps dual-language files out of the merge selection', async () => {
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                files: [
                    zhFile,
                    { fileName: 'dune.scjp.ass', directory: '/media/movies', languages: ['zh', 'ja'], format: 'ass', bytes: 8192 }
                ],
                embeddedScanned: true
            })
        )
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const selects = card.findAll('[data-test="subtitle-file-select"]')
        expect(selects[1].attributes('disabled')).toBeDefined()

        // a disabled select cannot join: the bar never appears
        await selects[0].trigger('click')
        await selects[1].trigger('click')
        expect(card.find('[data-test="bilingual-merge-bar"]').exists()).toBe(false)
        expect(mergeBilingualMock).not.toHaveBeenCalled()
    })

    it('toasts the readable server error and keeps the selection on failure', async () => {
        mergeBilingualMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: "both files carry the same language 'zh'" } }
        })
        const { wrapper, toastStore } = await mountDetail('movie', '/library/movie/7')

        const card = wrapper.find('[data-test="subtitle-files-card"]')
        const selects = card.findAll('[data-test="subtitle-file-select"]')
        await selects[0].trigger('click')
        await selects[1].trigger('click')
        await card.find('[data-test="bilingual-merge-action"]').trigger('click')
        await settle()

        expect(toastStore.toasts[0].type).toBe('error')
        expect(toastStore.toasts[0].message).toBe(
            en.mediaDetail.b06merge.failedToast.replace(
                '{message}',
                "both files carry the same language 'zh'"
            )
        )
        // the picks survive a failure so the user can retry or adjust
        expect(card.find('[data-test="bilingual-merge-bar"]').exists()).toBe(true)
    })
})

describe('MediaDetailPage · 字幕工具面板入口(B10 · 电影)', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        libraryMock.mockResolvedValue({
            items: [movieItem],
            totalCount: 1,
            pageNumber: 1,
            pageSize: 20
        })
        requestsMock.mockResolvedValue(emptyPage<ITranslationRequest>())
        getActiveTranslationsMock.mockResolvedValue([])
        mediaSubtitlesMock.mockResolvedValue(
            subtitlesPayload({
                files: [
                    {
                        fileName: 'dune.zh.srt',
                        directory: '/media/movies',
                        languages: ['zh'],
                        format: 'srt',
                        bytes: 2048
                    }
                ],
                embeddedScanned: true
            })
        )
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('the row tools button opens the panel with owner/targets/matched request', async () => {
        // the bilingual re-render input: the latest request whose written
        // output IS the row's file (translatedSubtitle path match)
        requestsMock.mockResolvedValue({
            items: [
                {
                    id: 4,
                    jobId: 'job-4',
                    title: 'Dune: Part Two',
                    sourceLanguage: 'en',
                    targetLanguage: 'zh',
                    translatedSubtitle: '/media/movies/dune.en.srt',
                    isBilingual: true,
                    mediaId: 7,
                    mediaType: 'Movie',
                    status: 'Completed',
                    source: 'Library',
                    progress: 100,
                    updatedAt: '2026-10-01T00:00:00Z',
                    createdAt: '2026-10-01T00:00:00Z'
                },
                {
                    id: 5,
                    jobId: 'job-5',
                    title: 'Dune: Part Two',
                    sourceLanguage: 'en',
                    targetLanguage: 'zh',
                    translatedSubtitle: '/media/movies/dune.zh.srt',
                    isBilingual: false,
                    mediaId: 7,
                    mediaType: 'Movie',
                    status: 'Completed',
                    source: 'Library',
                    progress: 100,
                    updatedAt: '2026-10-02T00:00:00Z',
                    createdAt: '2026-10-02T00:00:00Z'
                }
            ],
            totalCount: 2,
            pageNumber: 1,
            pageSize: 20
        })
        const { wrapper } = await mountDetail('movie', '/library/movie/7')

        // the row-level entry exists and opens the teleported panel
        const button = wrapper.find('[data-test="subtitle-file-tools"]')
        expect(button.exists()).toBe(true)
        await button.trigger('click')
        await settle()

        const modal = wrapper.findComponent(SubtitleToolsModal)
        expect(modal.exists()).toBe(true)
        expect(document.querySelector('[data-test="subtitle-tools-modal"]')).toBeTruthy()

        // the panel addresses the clicked row with the movie owner, the
        // effective targets, and the request that wrote this file (id 5 —
        // the path match wins over the newer-but-other-file request id 4)
        const props = modal.props() as {
            file: { fileName: string }
            owner: { mediaType: string; mediaId: number } | null
            targetLanguages: { code: string }[]
            request: { id: number } | null
        }
        expect(props.file.fileName).toBe('dune.zh.srt')
        expect(props.owner).toEqual({ mediaType: 'Movie', mediaId: 7 })
        expect(props.targetLanguages.map((language) => language.code)).toContain('ZH')
        expect(props.request?.id).toBe(5)

        // teleported DOM does not leak into later tests
        wrapper.unmount()
        expect(document.querySelector('[data-test="subtitle-tools-modal"]')).toBeNull()
    })
})
