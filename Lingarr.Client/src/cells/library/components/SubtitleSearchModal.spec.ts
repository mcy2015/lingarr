// @vitest-environment happy-dom
// SubtitleSearchModal — the manual subtitle search dialog. Covers the QA P0
// chain: per-source fan-out (provider required on the wire), unconfigured
// sources skipped but visible, candidates merged by arrival order,
// releaseInfo/size rendering, real skipTranslation semantics on the two
// download buttons, toast branching on translationRequestId, per-source
// failure rows with server messages, Escape close + abort of in-flight
// requests, and the query keyword actually reaching every request.
// P2 batch: the auto-score threshold reads subtitle_search_auto_score, the
// native-audio hint shows once per session, existing languages pre-mark
// their rows, downloads lock their row into a ✓ state, and failed sources
// re-fire individually with their own cacheKey.
// Any-language batch: the「+ 更多语言」picker expands the quick-toggle row
// with the culture-table full set (filter, 翻译源 markers, selections ride
// the wire), and the no-translation chrome (single button / badge / green
// track) anchors to the SETTINGS target set instead of the first checked
// language — non-target rows keep the dual download buttons.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { mount, type VueWrapper } from '@vue/test-utils'
import SubtitleSearchModal from '@/cells/library/components/SubtitleSearchModal.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import { useSettingStore } from '@/cells/app/stores/setting'
import { SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'
import type {
    ISubtitleCandidate,
    ISubtitleDownloadResponse,
    ISubtitleProviderHealthState
} from '@/types/services'

const getProvidersMock = vi.hoisted(() => vi.fn())
const searchMock = vi.hoisted(() => vi.fn())
const downloadMock = vi.hoisted(() => vi.fn())
const getSettingMock = vi.hoisted(() => vi.fn())
const getLanguagesMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: {
        getProviders: getProvidersMock,
        search: searchMock,
        download: downloadMock
    }
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({ getLanguages: getLanguagesMock })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({ getSetting: getSettingMock })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

/// The dialog teleports to document.body and test-utils does not unmount
/// wrappers automatically — unmount after each test and clear the body so
/// no dialog shadows the next test's document.querySelector.
let activeWrapper: VueWrapper | null = null

beforeEach(() => {
    vi.clearAllMocks()
    // seeded default of M0008 — individual tests override to move the line
    getSettingMock.mockResolvedValue('75')
    // culture-table stand-in (GET /api/translate/languages) for the
    // more-languages picker; only an expanded panel ever fetches it
    getLanguagesMock.mockResolvedValue([
        { code: 'zh', name: 'Chinese' },
        { code: 'en', name: 'English' },
        { code: 'it', name: 'Italian' },
        { code: 'ja', name: 'Japanese' },
        { code: 'zh-TW', name: 'Chinese (Traditional, Taiwan)' }
    ])
})

afterEach(() => {
    activeWrapper?.unmount()
    activeWrapper = null
    document.body.innerHTML = ''
})

/// All service continuations are microtasks; two macrotask ticks let the
/// whole getProviders → fan-out → settle chain drain without fake timers.
async function flush(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0))
    await new Promise((resolve) => setTimeout(resolve, 0))
}

// ── fixtures ──────────────────────────────────────────────────────
const manifest = (
    key: string,
    overrides: Partial<ISubtitleProviderHealthState> = {}
): ISubtitleProviderHealthState => ({
    provider: key,
    displayName: key.toUpperCase(),
    description: null,
    requiresAuth: false,
    credentialsConfigured: false,
    settings: [],
    cooldownUntil: null,
    cooldownReason: null,
    ...overrides
})

const opensubtitles = manifest('opensubtitles', {
    requiresAuth: true,
    credentialsConfigured: true
})
const assrt = manifest('assrt', { requiresAuth: true, credentialsConfigured: false })
const subsource = manifest('subsource')

const candidate = (overrides: Partial<ISubtitleCandidate> = {}): ISubtitleCandidate => ({
    provider: 'opensubtitles',
    externalId: 'ext-1',
    language: 'en',
    matchScore: 80,
    hashMatch: false,
    forced: false,
    hearingImpaired: false,
    format: 'srt',
    releaseInfo: 'Movie.2023.1080p.WEB-DL',
    uploader: 'uploader',
    size: 4096,
    ...overrides
})

const outcome = (
    cacheKey: string,
    candidates: ISubtitleCandidate[],
    webSearchUrls?: Record<string, string>
) => ({
    cacheKey,
    candidates,
    sourceOutcomes: [],
    ...(webSearchUrls ? { webSearchUrls } : {})
})

/// The backend answer also carries filesLanded (season-pack fan-out: N when
/// one download landed a file next to every episode of the season); the
/// shared wire-shape mirror in types/services predates it, so the spec
/// extends it locally.
type DownloadResponse = ISubtitleDownloadResponse & { filesLanded?: number }

const downloadResponse = (
    overrides: Partial<DownloadResponse> = {}
): DownloadResponse => ({
    historyId: 1,
    filePath: '/media/dune/dune.zh.srt',
    result: 'Downloaded',
    error: null,
    translationRequestId: null,
    translationDuplicate: false,
    filesLanded: 1,
    ...overrides
})

/// zh-only deployment for the download-semantics suites: their 'en'
/// candidates stay NON-target rows (dual buttons, no badge) after the
/// target-set alignment — the missing-setting fallback pair [zh, en] would
/// turn them into target rows.
const ZH_ONLY = { targetLanguages: [{ code: 'zh', name: 'Chinese' }] }

interface MountedModal {
    wrapper: VueWrapper
    toast: ReturnType<typeof useToastStore>
}

async function mountModal(
    props: {
        mediaType?: 'movie' | 'episode'
        mediaId?: number
        title?: string
        episode?: string
        audioLanguages?: string[] | null
        existingLanguages?: string[]
        /// Seeds target_languages in the setting store before mount — the
        /// language toggles derive from it (omit for the missing-setting
        /// fallback path; pass [] for the empty-list fallback path).
        targetLanguages?: { code: string; name: string }[]
        /// Seeds source_languages the same way — the picker's「翻译源」
        /// markers derive from it.
        sourceLanguages?: { code: string; name: string }[]
    } = {}
): Promise<MountedModal> {
    const { targetLanguages, sourceLanguages, ...componentProps } = props
    const pinia = createPinia()
    const setting = useSettingStore(pinia)
    if (targetLanguages) {
        setting.storeSetting(SETTINGS.TARGET_LANGUAGES, targetLanguages)
    }
    if (sourceLanguages) {
        setting.storeSetting(SETTINGS.SOURCE_LANGUAGES, sourceLanguages)
    }
    const wrapper = mount(SubtitleSearchModal, {
        props: {
            mediaType: 'movie',
            mediaId: 7,
            title: 'Dune: Part Two',
            ...componentProps
        },
        global: { plugins: [pinia, i18n] }
    })
    activeWrapper = wrapper
    await flush()
    return { wrapper, toast: useToastStore(pinia) }
}

const textOf = (selector: string): string =>
    document.querySelector(selector)?.textContent?.trim() ?? ''

const clickButton = async (label: string): Promise<void> => {
    const button = [...document.querySelectorAll('button')].find(
        (b) => b.textContent?.trim() === label
    )
    expect(button, `button "${label}" should render`).toBeDefined()
    button!.click()
    await flush()
}

describe('SubtitleSearchModal · 源扇出与状态', () => {
    it('issues one provider-scoped search per configured source and skips unconfigured ones visibly', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, assrt, subsource])
        searchMock.mockImplementation(async (request: { provider: string }) =>
            request.provider === 'opensubtitles'
                ? outcome('ck-os', [candidate()])
                : outcome('ck-sub', [candidate({ provider: 'subsource', externalId: 'sub-1' })])
        )
        await mountModal()

        // exactly one call per searchable source, each carrying its provider key
        expect(searchMock).toHaveBeenCalledTimes(2)
        const providers = searchMock.mock.calls.map((call) => call[0].provider)
        expect(providers).toEqual(['opensubtitles', 'subsource'])
        expect(providers).not.toContain('assrt')

        // the unconfigured source stays visible as "not configured"
        const assrtStatus = textOf('[data-test="source-status-assrt"]')
        expect(assrtStatus).toContain('ASSRT')
        expect(assrtStatus).toContain(en.subSearch.sourceSkipped)

        // configured sources report their real hit count
        expect(textOf('[data-test="source-status-opensubtitles"]')).toContain(
            en.subSearch.sourceOk.replace('{count}', '1')
        )

        // candidates of both sources render; meta counts settled sources over
        // the searchable total (the skipped one is visible in the strip above)
        expect(document.querySelectorAll('[data-test="candidate-row"]')).toHaveLength(2)
        expect(textOf('[data-test="search-meta"]')).toContain('2/2')
    })

    it('sends the movie/episode media identity verbatim to every source', async () => {
        getProvidersMock.mockResolvedValue([subsource])
        searchMock.mockResolvedValue(outcome('ck', []))
        await mountModal({ mediaType: 'episode', mediaId: 21, episode: 'S01E01' })

        expect(searchMock).toHaveBeenCalledWith(
            expect.objectContaining({ mediaType: 'episode', mediaId: 21 }),
            expect.any(AbortSignal)
        )
    })

    it('renders the honest no-source state when every source is unconfigured', async () => {
        getProvidersMock.mockResolvedValue([assrt])
        await mountModal()

        expect(searchMock).not.toHaveBeenCalled()
        expect(document.body.textContent).toContain(en.subSearch.noSources)
    })
})

describe('SubtitleSearchModal · 候选合并与渲染', () => {
    it('inserts arriving candidates by score without reshuffling rendered rows', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        let resolveSub!: (value: unknown) => void
        searchMock.mockImplementation((request: { provider: string }) => {
            if (request.provider === 'opensubtitles') {
                // arrives first, lower score
                return Promise.resolve(
                    outcome('ck-os', [candidate({ matchScore: 60, releaseInfo: 'Release.OS.1080p' })])
                )
            }
            return new Promise((resolve) => {
                resolveSub = resolve
            })
        })
        await mountModal()

        // first source settled: one row visible while the second is searching
        expect(document.querySelectorAll('[data-test="candidate-row"]')).toHaveLength(1)

        resolveSub(
            outcome('ck-sub', [
                candidate({ provider: 'subsource', externalId: 'sub-9', matchScore: 90, releaseInfo: null })
            ])
        )
        await flush()

        // the later, higher-scored candidate slots in above the rendered one
        const titles = [...document.querySelectorAll('[data-test="candidate-row"]')].map(
            (row) => row.querySelector('.truncate')?.textContent?.trim()
        )
        expect(titles).toEqual(['subsource:sub-9', 'Release.OS.1080p'])
    })

    it('keeps arrival order between equal scores', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        let resolveSub!: (value: unknown) => void
        searchMock.mockImplementation((request: { provider: string }) =>
            request.provider === 'opensubtitles'
                ? Promise.resolve(outcome('ck-os', [candidate({ matchScore: 80, releaseInfo: 'OS-first' })]))
                : new Promise((resolve) => {
                      resolveSub = resolve
                  })
        )
        await mountModal()
        resolveSub(
            outcome('ck-sub', [
                candidate({ provider: 'subsource', externalId: 'sub-2', matchScore: 80, releaseInfo: null })
            ])
        )
        await flush()

        const titles = [...document.querySelectorAll('[data-test="candidate-row"]')].map(
            (row) => row.querySelector('.truncate')?.textContent?.trim()
        )
        expect(titles).toEqual(['OS-first', 'subsource:sub-2'])
    })

    it('shows releaseInfo as title with the provider:id fallback, and the formatted byte size', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ releaseInfo: 'Dune.Part.Two.2023.2160p.UHD.BluRay', size: 1536 }),
                candidate({ externalId: 'ext-raw', releaseInfo: null, size: null })
            ])
        )
        await mountModal()

        const rows = [...document.querySelectorAll('[data-test="candidate-row"]')]
        expect(rows).toHaveLength(2)
        expect(rows[0].textContent).toContain('Dune.Part.Two.2023.2160p.UHD.BluRay')
        expect(rows[0].textContent).toContain('1.5 KB')
        // null releaseInfo falls back to provider:externalId, null size to —
        expect(rows[1].textContent).toContain('opensubtitles:ext-raw')
        expect(rows[1].textContent).toContain('—')
    })
})

describe('SubtitleSearchModal · 下载语义与 toast', () => {
    beforeEach(() => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [candidate({ language: 'en', externalId: 'ext-dl' })])
        )
    })

    it('sends skipTranslation true from「仅下载」and false from「下载并翻译」', async () => {
        downloadMock.mockResolvedValue(downloadResponse())
        // two distinct rows: a successful download locks its row into the ✓
        // state (P2), so each button is exercised from its own unlocked row
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ language: 'en', externalId: 'ext-dl-only' }),
                candidate({ language: 'en', externalId: 'ext-dl-translate' })
            ])
        )
        await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        await clickButton(en.subSearch.downloadAndTranslate)

        expect(downloadMock).toHaveBeenCalledTimes(2)
        // both address the candidate through its own source's cache + index
        expect(downloadMock).toHaveBeenNthCalledWith(
            1,
            expect.objectContaining({
                cacheKey: 'ck-os',
                candidateIndex: 0,
                mediaType: 'movie',
                mediaId: 7,
                source: 'Manual',
                skipTranslation: true
            }),
            expect.any(AbortSignal)
        )
        expect(downloadMock).toHaveBeenNthCalledWith(
            2,
            expect.objectContaining({ skipTranslation: false }),
            expect.any(AbortSignal)
        )
    })

    it('toasts "queued" when the response carries a translationRequestId, plain saved otherwise', async () => {
        downloadMock.mockResolvedValue(downloadResponse({ translationRequestId: 55 }))
        const { toast, wrapper } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadAndTranslate)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadedAndQueued.replace('{file}', '/media/dune/dune.zh.srt')
        )
        expect(toast.toasts[0].type).toBe('success')
        expect(wrapper.emitted('downloaded')).toEqual([['/media/dune/dune.zh.srt']])
    })

    it('toasts plain saved when no translation was queued', async () => {
        downloadMock.mockResolvedValue(downloadResponse())
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloaded.replace('{file}', '/media/dune/dune.zh.srt')
        )
    })

    it('toasts the matched-episode count when a whole-season archive landed', async () => {
        // Season-pack fan-out: filesLanded > 1 replaces the single-file
        // toast with the matched-count one (the downloaded event still
        // carries the initiating episode's file).
        downloadMock.mockResolvedValue(downloadResponse({ filesLanded: 8 }))
        const { toast, wrapper } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadedSeasonPack.replace('{count}', '8')
        )
        expect(toast.toasts[0].type).toBe('success')
        expect(wrapper.emitted('downloaded')).toEqual([['/media/dune/dune.zh.srt']])
    })

    it('keeps the single-file toast when exactly one file landed', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ filesLanded: 1, translationRequestId: 55 })
        )
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadAndTranslate)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadedAndQueued.replace('{file}', '/media/dune/dune.zh.srt')
        )
    })

    it('flags a duplicate translation request', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ translationRequestId: 9, translationDuplicate: true })
        )
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadAndTranslate)
        expect(toast.toasts[0].message).toBe(en.subSearch.translationDuplicate)
        expect(toast.toasts[0].type).toBe('info')
    })

    it('surfaces the server result and error when the file did not land', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ filePath: '', result: 'Skipped', error: 'target already covered' })
        )
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadRejected
                .replace('{result}', 'Skipped')
                .replace('{error}', 'target already covered')
        )
        expect(toast.toasts[0].type).toBe('error')
    })

    it('toasts the localized retry moment on a CooldownSkipped answer', async () => {
        // assrt 493 closure: inside the rate-limit window the server skips
        // the upstream fetch and answers the retry moment — the toast names
        // the source and quotes HH:MM in the viewer's local zone.
        const untilIso = '2026-10-20T09:41:00.000Z'
        downloadMock.mockResolvedValue(
            downloadResponse({
                filePath: '',
                result: 'CooldownSkipped',
                error: null,
                cooldownUntilUtc: untilIso
            })
        )
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        const localHm = (() => {
            const date = new Date(untilIso)
            return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
        })()
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadCooldown
                .replace('{provider}', 'opensubtitles')
                .replace('{time}', localHm)
        )
        expect(toast.toasts[0].type).toBe('info')
        // nothing landed: the row stays unlocked for a retry after the window
        const buttons = [...document.querySelectorAll('[data-test="candidate-row"] button')].filter(
            (b) => !b.hasAttribute('aria-label')
        )
        expect(buttons.length).toBeGreaterThan(0)
        expect(buttons.every((b) => !(b as HTMLButtonElement).disabled)).toBe(true)
    })

    it('falls back to the no-moment cooldown copy when the response carries no timestamp', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ filePath: '', result: 'CooldownSkipped', error: null })
        )
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadCooldownPending.replace('{provider}', 'opensubtitles')
        )
        expect(toast.toasts[0].type).toBe('info')
    })

    it('extracts the server message when the download request throws', async () => {
        downloadMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'the search result expired' } }
        })
        const { toast } = await mountModal(ZH_ONLY)

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe('the search result expired')
        expect(toast.toasts[0].type).toBe('error')
    })
})

describe('SubtitleSearchModal · 源失败与错误呈现', () => {
    it('records the failed source row without a global toast while another source works', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        searchMock.mockImplementation((request: { provider: string }) =>
            request.provider === 'opensubtitles'
                ? Promise.reject({
                      isAxiosError: true,
                      message: 'Network Error',
                      response: { data: { message: 'invalid credentials' } }
                  })
                : Promise.resolve(outcome('ck-sub', [candidate({ provider: 'subsource' })]))
        )
        const { toast } = await mountModal()

        const status = textOf('[data-test="source-status-opensubtitles"]')
        expect(status).toContain(en.subSearch.sourceError.replace('{message}', 'invalid credentials'))
        // partial success: candidates render, no global error toast
        expect(document.querySelectorAll('[data-test="candidate-row"]')).toHaveLength(1)
        expect(toast.toasts).toHaveLength(0)
    })

    it('toasts the server message once when every source fails', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        searchMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'quota exceeded' } }
        })
        const { toast } = await mountModal()

        expect(toast.toasts).toHaveLength(1)
        expect(toast.toasts[0].message).toBe('quota exceeded')
        expect(toast.toasts[0].type).toBe('error')
        expect(document.body.textContent).toContain(en.subSearch.noResults)
    })
})

describe('SubtitleSearchModal · 关闭、取消与可达性', () => {
    it('closes on Escape and aborts every in-flight search', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        const signals: AbortSignal[] = []
        searchMock.mockImplementation((_request: unknown, signal: AbortSignal) => {
            signals.push(signal)
            return new Promise(() => {})
        })
        const { wrapper, toast } = await mountModal()

        expect(searchMock).toHaveBeenCalledTimes(2)
        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
        await flush()

        expect(wrapper.emitted('close')).toHaveLength(1)
        expect(signals).toHaveLength(2)
        for (const signal of signals) expect(signal.aborted).toBe(true)
        // an aborted round never toasts into a closed dialog
        expect(toast.toasts).toHaveLength(0)
    })

    it('marks the dialog aria-modal and moves focus into it on open', async () => {
        getProvidersMock.mockResolvedValue([])
        await mountModal()

        const dialog = document.querySelector('[role="dialog"]') as HTMLElement | null
        expect(dialog).not.toBeNull()
        expect(dialog!.getAttribute('aria-modal')).toBe('true')
        expect(document.activeElement).toBe(dialog)
    })
})

describe('SubtitleSearchModal · 关键词', () => {
    it('passes the typed query to every per-source request of the new round', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        searchMock.mockResolvedValue(outcome('ck', []))
        await mountModal()

        // initial round sends the prefilled title as the keyword — the
        // input's visible content is exactly what travels on the wire
        const initial = searchMock.mock.calls.map((call) => call[0].query)
        expect(initial).toEqual(['Dune: Part Two', 'Dune: Part Two'])

        const input = document.querySelector(
            '[data-test="subsearch-query-input"]'
        ) as HTMLInputElement
        input.value = 'dune 2160p'
        input.dispatchEvent(new Event('input'))
        await clickButton(en.subSearch.searchAction)

        expect(searchMock).toHaveBeenCalledTimes(4)
        const reloaded = searchMock.mock.calls.slice(2).map((call) => call[0].query)
        expect(reloaded).toEqual(['dune 2160p', 'dune 2160p'])
    })
})

// ═══ P2 batch: auto-score threshold reads the setting ═════════════
describe('SubtitleSearchModal · 分数线读设置', () => {
    it('warns below the configured line only, and quotes it in the hint text', async () => {
        getSettingMock.mockResolvedValue('60')
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ matchScore: 60, externalId: 'at-line' }),
                candidate({ matchScore: 59, externalId: 'below-line' })
            ])
        )
        await mountModal()

        const rows = [...document.querySelectorAll('[data-test="candidate-row"]')]
        expect(rows).toHaveLength(2)

        // score-descending merge: 60 sits above 59
        const scores = rows.map((row) => row.querySelector('.w-8')?.textContent?.trim())
        expect(scores).toEqual(['60', '59'])

        // 60 is NOT below the configured line — no warning colour, no title
        expect(rows[0].querySelector('.w-8')?.classList.contains('text-warning')).toBe(false)
        // 59 IS below it: warning colour + parameterized hint quoting 60
        const below = rows[1].querySelector('.w-8')
        expect(below?.classList.contains('text-warning')).toBe(true)
        expect(below?.getAttribute('title')).toBe(
            en.subSearch.lowConfidence.replace('{score}', '60')
        )
    })

    it('falls back to the default 75 when the setting is missing or invalid', async () => {
        getSettingMock.mockRejectedValue({ isAxiosError: true, response: { status: 400 } })
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [candidate({ matchScore: 74, externalId: 'edge' })])
        )
        await mountModal()

        const score = document.querySelector('[data-test="candidate-row"] .w-8')
        expect(score?.classList.contains('text-warning')).toBe(true)
        expect(score?.getAttribute('title')).toBe(
            en.subSearch.lowConfidence.replace('{score}', '75')
        )
    })
})

// ═══ P2 batch: native-audio one-time hint ═════════════════════════
describe('SubtitleSearchModal · 音轨覆盖提示', () => {
    beforeEach(() => {
        getProvidersMock.mockResolvedValue([subsource])
        searchMock.mockResolvedValue(outcome('ck-sub', []))
    })

    it('shows the hint when the audio covers a searched language, and closes it for the session', async () => {
        const { wrapper } = await mountModal({ audioLanguages: ['zh', 'en'] })

        const hint = document.querySelector('[data-test="native-audio-hint"]')
        expect(hint).not.toBeNull()
        expect(hint!.textContent).toContain(en.subSearch.nativeAudioHint)
        expect(hint!.textContent).toContain('still search') // 仍可搜索 semantics

        // close button dismisses for this modal session…
        ;(
            hint!.querySelector('[data-test="native-audio-hint-close"]') as HTMLButtonElement
        ).click()
        await flush()
        expect(document.querySelector('[data-test="native-audio-hint"]')).toBeNull()

        // …including across a new search round inside the same session
        await clickButton(en.subSearch.searchAction)
        expect(document.querySelector('[data-test="native-audio-hint"]')).toBeNull()
        expect(wrapper.emitted('close')).toBeUndefined() // the × never closes the dialog
    })

    it('renders no hint for audio without a searched language or unknown audio', async () => {
        await mountModal({ audioLanguages: ['ja'] })
        expect(document.querySelector('[data-test="native-audio-hint"]')).toBeNull()

        await mountModal({ audioLanguages: null })
        expect(document.querySelector('[data-test="native-audio-hint"]')).toBeNull()
    })
})

// ═══ language toggles derive from target_languages ════════════════
describe('SubtitleSearchModal · 语言钮从 target_languages 派生', () => {
    beforeEach(() => {
        getProvidersMock.mockResolvedValue([subsource])
        searchMock.mockResolvedValue(outcome('ck-sub', []))
    })

    /// The language toggles are the only aria-pressed buttons in the dialog.
    const toggles = (): HTMLButtonElement[] =>
        [...document.querySelectorAll('button[aria-pressed]')] as HTMLButtonElement[]

    it('renders one all-checked toggle per configured target language', async () => {
        await mountModal({
            targetLanguages: [
                { code: 'zh', name: 'Chinese' },
                { code: 'ja', name: 'Japanese' }
            ]
        })

        // ZH/JA toggles, all checked by default, no EN toggle anywhere
        expect(toggles().map((b) => b.textContent?.trim())).toEqual(['ZH', 'JA'])
        expect(toggles().every((b) => b.getAttribute('aria-pressed') === 'true')).toBe(true)

        // the derived pair rides the wire on every request of the round
        for (const call of searchMock.mock.calls) {
            expect(call[0].languages).toEqual(['zh', 'ja'])
        }
    })

    it('rides the checked languages on the wire after a toggle', async () => {
        await mountModal({
            targetLanguages: [
                { code: 'zh', name: 'Chinese' },
                { code: 'ja', name: 'Japanese' }
            ]
        })
        searchMock.mockClear()

        // uncheck ZH — only JA stays checked, the new round sends ['ja']
        ;(toggles().find((b) => b.textContent?.trim() === 'ZH') as HTMLButtonElement).click()
        await flush()
        await clickButton(en.subSearch.searchAction)

        expect(searchMock).toHaveBeenCalledTimes(1)
        expect(searchMock.mock.calls[0][0].languages).toEqual(['ja'])
    })

    it('falls back to zh/en when the setting is missing or empty', async () => {
        await mountModal()
        expect(toggles().map((b) => b.textContent?.trim())).toEqual(['ZH', 'EN'])
        expect(searchMock.mock.calls[0][0].languages).toEqual(['zh', 'en'])

        // an empty configured list falls back identically
        activeWrapper?.unmount()
        activeWrapper = null
        document.body.innerHTML = ''
        searchMock.mockClear()
        await mountModal({ targetLanguages: [] })
        expect(toggles().map((b) => b.textContent?.trim())).toEqual(['ZH', 'EN'])
        expect(searchMock.mock.calls[0][0].languages).toEqual(['zh', 'en'])
    })

    it('normalizes configured codes to the lower-case wire form', async () => {
        // settings may hold upper-case codes (the settings screen writes
        // what the user picked) — the wire stays lower-case
        await mountModal({ targetLanguages: [{ code: 'ZH', name: 'Chinese' }] })

        expect(toggles().map((b) => b.textContent?.trim())).toEqual(['ZH'])
        expect(searchMock.mock.calls[0][0].languages).toEqual(['zh'])
    })
})

// ═══ P2 batch: existing-language pre-marks ════════════════════════
describe('SubtitleSearchModal · 已存在预标', () => {
    it('weakens rows whose language the media already carries and keeps the buttons usable', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ language: 'zh', externalId: 'zh-existing' }),
                candidate({ language: 'en', externalId: 'en-new' })
            ])
        )
        await mountModal({ existingLanguages: ['zh'] })

        const rows = [...document.querySelectorAll('[data-test="candidate-row"]')]
        expect(rows).toHaveLength(2)

        // the zh row carries the badge and the weakened (opacity) state
        const existingRow = rows[0]
        expect(existingRow.querySelector('[data-test="existing-badge"]')?.textContent?.trim()).toBe(
            en.subSearch.existing
        )
        expect(existingRow.classList.contains('opacity-60')).toBe(true)

        // manual re-download stays legal: the button is NOT disabled
        const existingButton = existingRow.querySelector('button:not([aria-label])')
        expect(existingButton?.hasAttribute('disabled')).toBe(false)

        // the en row is unmarked and full-strength
        expect(rows[1].querySelector('[data-test="existing-badge"]')).toBeNull()
        expect(rows[1].classList.contains('opacity-60')).toBe(false)
    })
})

// ═══ P2 batch: download ✓ row state ═══════════════════════════════
describe('SubtitleSearchModal · 下载成功 ✓ 态', () => {
    it('locks the row after a successful download and unlocks on the next round', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [candidate({ language: 'en', externalId: 'ext-dl' })])
        )
        downloadMock.mockResolvedValue(downloadResponse())
        await mountModal(ZH_ONLY)

        const rowOf = (): HTMLElement =>
            document.querySelector('[data-test="candidate-row"]') as HTMLElement
        const buttonsOf = (): HTMLButtonElement[] =>
            [...rowOf().querySelectorAll('button')].filter((b) => !b.hasAttribute('aria-label'))
        expect(buttonsOf().every((b) => !b.disabled)).toBe(true)

        await clickButton(en.subSearch.downloadOnly)

        // the file landed: both row buttons disable and flip to ✓ Downloaded
        const locked = buttonsOf()
        expect(locked).toHaveLength(2)
        expect(locked.every((b) => b.disabled)).toBe(true)
        expect(locked.every((b) => b.textContent?.trim() === `✓ ${en.subSearch.downloadedMark}`)).toBe(
            true
        )

        // a new search round clears the session's downloaded set
        await clickButton(en.subSearch.searchAction)
        const fresh = buttonsOf()
        expect(fresh.every((b) => !b.disabled)).toBe(true)
        expect(fresh[0].textContent?.trim()).toBe(en.subSearch.downloadOnly)
    })
})

// ═══ P2 batch: per-source retry ═══════════════════════════════════
describe('SubtitleSearchModal · 单源重试', () => {
    it('re-fires only the failed provider and merges its candidates under their own cacheKey', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles, subsource])
        searchMock.mockImplementation((request: { provider: string }) =>
            request.provider === 'opensubtitles'
                ? Promise.reject({
                      isAxiosError: true,
                      message: 'Network Error',
                      response: { data: { message: 'invalid credentials' } }
                  })
                : Promise.resolve(outcome('ck-sub', [candidate({ provider: 'subsource' })]))
        )
        await mountModal(ZH_ONLY)

        // the failed source exposes the retry button, the ok one does not
        expect(
            document.querySelector('[data-test="source-retry-opensubtitles"]')
        ).not.toBeNull()
        expect(document.querySelector('[data-test="source-retry-subsource"]')).toBeNull()

        // retry answers with fresh candidates carrying their own cache key
        searchMock.mockResolvedValue(
            outcome('ck-os-retry', [
                candidate({
                    provider: 'opensubtitles',
                    externalId: 'os-9',
                    matchScore: 95,
                    releaseInfo: 'Release.Retry.2160p'
                })
            ])
        )
        ;(
            document.querySelector(
                '[data-test="source-retry-opensubtitles"]'
            ) as HTMLButtonElement
        ).click()
        await flush()

        // exactly one extra search, addressed to the failed provider only
        expect(searchMock).toHaveBeenCalledTimes(3)
        expect(searchMock).toHaveBeenLastCalledWith(
            expect.objectContaining({ provider: 'opensubtitles' }),
            expect.any(AbortSignal)
        )

        // the retried candidates merged by the ordinary rule (95 on top)
        const titles = [...document.querySelectorAll('[data-test="candidate-row"]')].map(
            (row) => row.querySelector('.truncate')?.textContent?.trim()
        )
        expect(titles).toEqual(['Release.Retry.2160p', 'Movie.2023.1080p.WEB-DL'])
        // the source strip reports the recovered state honestly
        expect(textOf('[data-test="source-status-opensubtitles"]')).toContain(
            en.subSearch.sourceOk.replace('{count}', '1')
        )

        // and the retried candidate downloads through its own cacheKey
        downloadMock.mockResolvedValue(downloadResponse())
        await clickButton(en.subSearch.downloadAndTranslate)
        expect(downloadMock).toHaveBeenCalledWith(
            expect.objectContaining({ cacheKey: 'ck-os-retry', candidateIndex: 0 }),
            expect.any(AbortSignal)
        )
    })

    it('keeps the error row honest when the retry fails again', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'cooldown active' } }
        })
        await mountModal()

        ;(
            document.querySelector(
                '[data-test="source-retry-opensubtitles"]'
            ) as HTMLButtonElement
        ).click()
        await flush()

        const status = textOf('[data-test="source-status-opensubtitles"]')
        expect(status).toContain(en.subSearch.sourceError.replace('{message}', 'cooldown active'))
        // the retry button stays available for another attempt
        expect(
            document.querySelector('[data-test="source-retry-opensubtitles"]')
        ).not.toBeNull()
    })
})

// ═══ any-language picker: culture-table expansion of the toggle row ═══
describe('SubtitleSearchModal · 任意语言选择器', () => {
    beforeEach(() => {
        getProvidersMock.mockResolvedValue([subsource])
        searchMock.mockResolvedValue(outcome('ck-sub', []))
    })

    /// The quick-toggle row keeps only language toggles — the more-languages
    /// entry is an expand switch, not a toggle (no aria-pressed).
    const langToggles = (): HTMLButtonElement[] =>
        [...document.querySelectorAll('button[aria-pressed]')] as HTMLButtonElement[]

    const clickTest = async (selector: string): Promise<void> => {
        const element = document.querySelector(selector) as HTMLElement | null
        expect(element, `${selector} should render`).not.toBeNull()
        element!.click()
        await flush()
    }

    it('renders only the target toggles by default, the picker entry collapsed and unfetched', async () => {
        await mountModal({ targetLanguages: [{ code: 'zh', name: 'Chinese' }] })

        expect(langToggles().map((b) => b.textContent?.trim())).toEqual(['ZH'])
        expect(langToggles().every((b) => b.getAttribute('aria-pressed') === 'true')).toBe(true)

        const entry = document.querySelector('[data-test="subsearch-more-languages"]')
        expect(entry).not.toBeNull()
        expect(entry!.getAttribute('aria-expanded')).toBe('false')
        expect(entry!.textContent).toContain(en.subSearch.moreLanguages)
        expect(document.querySelector('[data-test="subsearch-language-panel"]')).toBeNull()
        // the culture table loads lazily — a closed picker never fetches it
        expect(getLanguagesMock).not.toHaveBeenCalled()
    })

    it('appends a picked culture-table language as a checked quick toggle and rides it on the wire', async () => {
        await mountModal({ targetLanguages: [{ code: 'zh', name: 'Chinese' }] })
        searchMock.mockClear()

        await clickTest('[data-test="subsearch-more-languages"]')
        expect(document.querySelector('[data-test="subsearch-language-panel"]')).not.toBeNull()
        expect(getLanguagesMock).toHaveBeenCalledTimes(1)

        // the target language renders checked inside the panel, en unchecked
        expect(
            document.querySelector('[data-test="culture-option-zh"]')!.getAttribute('aria-selected')
        ).toBe('true')
        expect(
            document.querySelector('[data-test="culture-option-en"]')!.getAttribute('aria-selected')
        ).toBe('false')

        await clickTest('[data-test="culture-option-ja"]')

        // JA joined the quick row after the target-derived toggle, checked
        expect(langToggles().map((b) => b.textContent?.trim())).toEqual(['ZH', 'JA'])
        expect(langToggles()[1].getAttribute('aria-pressed')).toBe('true')

        await clickButton(en.subSearch.searchAction)
        expect(searchMock).toHaveBeenCalledTimes(1)
        expect(searchMock.mock.calls[0][0].languages).toEqual(['zh', 'ja'])
    })

    it('unchecking an appended quick toggle removes it from the wire but keeps the button', async () => {
        await mountModal({ targetLanguages: [{ code: 'zh', name: 'Chinese' }] })
        await clickTest('[data-test="subsearch-more-languages"]')
        await clickTest('[data-test="culture-option-ja"]')
        searchMock.mockClear()

        ;(langToggles().find((b) => b.textContent?.trim() === 'JA') as HTMLButtonElement).click()
        await flush()
        await clickButton(en.subSearch.searchAction)

        expect(searchMock).toHaveBeenCalledTimes(1)
        expect(searchMock.mock.calls[0][0].languages).toEqual(['zh'])
        // the appended button itself stays for a re-check, now unchecked
        const jaToggle = langToggles().find((b) => b.textContent?.trim() === 'JA')
        expect(jaToggle).toBeDefined()
        expect(jaToggle!.getAttribute('aria-pressed')).toBe('false')
    })

    it('marks translation-source options and narrows the list through the filter', async () => {
        await mountModal({
            targetLanguages: [{ code: 'zh', name: 'Chinese' }],
            sourceLanguages: [{ code: 'en', name: 'English' }]
        })
        await clickTest('[data-test="subsearch-more-languages"]')

        expect(
            document.querySelector('[data-test="culture-source-en"]')?.textContent?.trim()
        ).toBe(en.subSearch.translationSource)
        expect(document.querySelector('[data-test="culture-source-ja"]')).toBeNull()

        const filter = document.querySelector(
            '[data-test="subsearch-language-filter"]'
        ) as HTMLInputElement
        filter.value = 'ital'
        filter.dispatchEvent(new Event('input'))
        await flush()
        expect(document.querySelector('[data-test="culture-option-it"]')).not.toBeNull()
        expect(document.querySelector('[data-test="culture-option-ja"]')).toBeNull()

        filter.value = 'zzz'
        filter.dispatchEvent(new Event('input'))
        await flush()
        expect(document.querySelector('[data-test="culture-option-it"]')).toBeNull()
        expect(textOf('[data-test="subsearch-language-panel"]')).toContain(
            en.subSearch.languageNoResults
        )
    })
})

// ═══ no-translation judgment anchored to the settings target set ═══
describe('SubtitleSearchModal · 免翻译判定锚定 target 集', () => {
    it('gives target rows the single button, badge and green track; non-target rows dual buttons', async () => {
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ language: 'zh', externalId: 'zh-row', releaseInfo: 'Row.ZH' }),
                candidate({ language: 'ja', externalId: 'ja-row', releaseInfo: 'Row.JA' })
            ])
        )
        await mountModal({ targetLanguages: [{ code: 'zh', name: 'Chinese' }] })

        const rows = [...document.querySelectorAll('[data-test="candidate-row"]')]
        const zhRow = rows.find((row) => row.textContent?.includes('Row.ZH'))!
        const jaRow = rows.find((row) => row.textContent?.includes('Row.JA'))!
        const buttonsOf = (row: Element): string[] =>
            [...row.querySelectorAll('button')]
                .filter((b) => !b.hasAttribute('aria-label'))
                .map((b) => b.textContent?.trim() ?? '')

        // zh (target): single「下载」button, no-translate badge, green track
        expect(buttonsOf(zhRow)).toEqual([en.subSearch.download])
        expect(zhRow.textContent).toContain(en.subSearch.noTranslate)
        expect(zhRow.querySelector('[data-test="candidate-track"]')!.classList.contains('bg-success')).toBe(true)

        // ja (non-target): dual buttons, no badge, info track
        expect(buttonsOf(jaRow)).toEqual([
            en.subSearch.downloadOnly,
            en.subSearch.downloadAndTranslate
        ])
        expect(jaRow.textContent).not.toContain(en.subSearch.noTranslate)
        expect(jaRow.querySelector('[data-test="candidate-track"]')!.classList.contains('bg-info')).toBe(true)
        expect(jaRow.querySelector('[data-test="candidate-track"]')!.classList.contains('bg-success')).toBe(false)
    })

    it('keeps the target row single-button even when its toggle is unchecked', async () => {
        // the exemption judges the SETTINGS target set, never the currently
        // checked set — unchecking ZH must not demote the zh row
        getProvidersMock.mockResolvedValue([opensubtitles])
        searchMock.mockResolvedValue(
            outcome('ck-os', [
                candidate({ language: 'zh', externalId: 'zh-row', releaseInfo: 'Row.ZH' })
            ])
        )
        await mountModal({ targetLanguages: [{ code: 'zh', name: 'Chinese' }] })

        const zhToggle = [...document.querySelectorAll('button[aria-pressed]')].find(
            (b) => b.textContent?.trim() === 'ZH'
        ) as HTMLButtonElement
        zhToggle.click()
        await flush()

        const row = document.querySelector('[data-test="candidate-row"]')!
        const buttons = [...row.querySelectorAll('button')]
            .filter((b) => !b.hasAttribute('aria-label'))
            .map((b) => b.textContent?.trim() ?? '')
        expect(buttons).toEqual([en.subSearch.download])
        expect(row.textContent).toContain(en.subSearch.noTranslate)
    })
})

// ═══ web-search escape hatch: per-source external links ═══════════
describe('SubtitleSearchModal · 网站搜索外链', () => {
    /// The full deep-link map the backend derives from one media-level
    /// request (episode form, "Severance" S01E02) — every per-source
    /// response carries it, so any sibling's arrival fills links for the
    /// whole strip.
    const episodeWebUrls = (): Record<string, string> => ({
        assrt: 'https://assrt.net/sub/?searchword=Severance%20S01E02',
        opensubtitles:
            'https://www.opensubtitles.com/en/search-tvshows/q-Severance/season-1/episode-2',
        subdl: 'https://subdl.com/search/Severance',
        subsource: 'https://subsource.net/search?q=Severance',
        gestdown: 'https://www.addic7ed.com/search.php?search=Severance&Submit=Search'
    })

    it('renders the external link on every feasible source chip and none for xunlei/shooter', async () => {
        getProvidersMock.mockResolvedValue([
            manifest('opensubtitles', {
                requiresAuth: true,
                credentialsConfigured: true,
                websiteUrl: 'https://www.opensubtitles.com'
            }),
            manifest('assrt', {
                requiresAuth: true,
                credentialsConfigured: true,
                websiteUrl: 'https://assrt.net'
            }),
            manifest('subdl', { websiteUrl: 'https://subdl.com' }),
            manifest('subsource', { websiteUrl: 'https://subsource.net' }),
            manifest('gestdown', { websiteUrl: 'https://www.addic7ed.com' }),
            // no public search page → no websiteUrl, no link, ever
            manifest('xunlei', { requiresAuth: true, credentialsConfigured: false }),
            manifest('shooter', { requiresAuth: true, credentialsConfigured: false })
        ])
        searchMock.mockImplementation(async (request: { provider: string }) =>
            outcome('ck-' + request.provider, [], episodeWebUrls())
        )
        await mountModal({ mediaType: 'episode', title: 'Severance', episode: 'S01E02' })

        const expected = episodeWebUrls()
        for (const key of Object.keys(expected)) {
            const anchor = document.querySelector(
                `[data-test="source-web-${key}"]`
            ) as HTMLAnchorElement | null
            expect(anchor, `link of ${key} should render`).not.toBeNull()
            expect(anchor!.getAttribute('href')).toBe(expected[key])
            // safe external navigation + an honest, localized label
            expect(anchor!.getAttribute('target')).toBe('_blank')
            expect(anchor!.getAttribute('rel')).toBe('noopener noreferrer')
            expect(anchor!.getAttribute('aria-label')).toBe(
                `${key.toUpperCase()} ${en.subSearch.openInSite}`
            )
            expect(anchor!.textContent).toContain('↗')
        }
        expect(document.querySelector('[data-test="source-web-xunlei"]')).toBeNull()
        expect(document.querySelector('[data-test="source-web-shooter"]')).toBeNull()
    })

    it('keeps the link through zero-hit, error and skipped states (skipped falls back to the homepage)', async () => {
        getProvidersMock.mockResolvedValue([
            // zero-hit source: ok state with no candidates
            manifest('subdl', { websiteUrl: 'https://subdl.com' }),
            // skipped: never searched, only the manifest homepage exists
            manifest('assrt', { requiresAuth: true, credentialsConfigured: false, websiteUrl: 'https://assrt.net' }),
            manifest('subsource', { websiteUrl: 'https://subsource.net' })
        ])
        const webUrls = episodeWebUrls()
        // the sibling responses carry links for subdl/subsource only — assrt
        // has no map entry and must fall back to its homepage
        delete webUrls.assrt
        searchMock.mockImplementation((request: { provider: string }) =>
            request.provider === 'subsource'
                ? Promise.reject({
                      isAxiosError: true,
                      message: 'Network Error',
                      response: { data: { message: 'invalid credentials' } }
                  })
                : Promise.resolve(outcome('ck-' + request.provider, [], webUrls))
        )
        await mountModal({ mediaType: 'episode', title: 'Severance', episode: 'S01E02' })

        // zero hits: the link survives (still the response's deep link)
        const subdl = document.querySelector('[data-test="source-web-subdl"]') as HTMLAnchorElement
        expect(subdl.getAttribute('href')).toBe(webUrls.subdl)

        // errored source: the link survives on the error chip too
        const subsource = document.querySelector(
            '[data-test="source-web-subsource"]'
        ) as HTMLAnchorElement
        expect(subsource.getAttribute('href')).toBe(webUrls.subsource)
        expect(
            document
                .querySelector('[data-test="source-status-subsource"]')
                ?.textContent
        ).toContain(en.subSearch.sourceError.replace('{message}', 'invalid credentials'))

        // skipped source: homepage fallback when no sibling delivered its link
        const assrt = document.querySelector('[data-test="source-web-assrt"]') as HTMLAnchorElement
        expect(assrt.getAttribute('href')).toBe('https://assrt.net')
        expect(
            document.querySelector('[data-test="source-status-assrt"]')?.textContent
        ).toContain(en.subSearch.sourceSkipped)
    })

    it('replaces the links when a new round carries a Query override', async () => {
        getProvidersMock.mockResolvedValue([manifest('assrt', { requiresAuth: true, credentialsConfigured: true })])
        searchMock.mockResolvedValue(outcome('ck-1', [], episodeWebUrls()))
        await mountModal({ mediaType: 'episode', title: 'Severance', episode: 'S01E02' })
        expect(
            (document.querySelector('[data-test="source-web-assrt"]') as HTMLAnchorElement).getAttribute('href')
        ).toBe('https://assrt.net/sub/?searchword=Severance%20S01E02')

        // the override round answers with links built from the override
        // term — the chip must show exactly what the new responses carry
        searchMock.mockResolvedValue(
            outcome('ck-2', [], {
                assrt: 'https://assrt.net/sub/?searchword=Loki%20S01E01'
            })
        )
        const input = document.querySelector(
            '[data-test="subsearch-query-input"]'
        ) as HTMLInputElement
        input.value = 'Loki S01E01'
        input.dispatchEvent(new Event('input'))
        await clickButton(en.subSearch.searchAction)

        expect(
            (document.querySelector('[data-test="source-web-assrt"]') as HTMLAnchorElement).getAttribute('href')
        ).toBe('https://assrt.net/sub/?searchword=Loki%20S01E01')
    })
})
