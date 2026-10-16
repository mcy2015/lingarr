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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { mount, type VueWrapper } from '@vue/test-utils'
import SubtitleSearchModal from '@/cells/library/components/SubtitleSearchModal.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import en from '@/i18n/locales/en.json'
import type { ISubtitleProviderManifest } from '@/types/plugin'
import type { ISubtitleCandidate, ISubtitleDownloadResponse } from '@/types/services'

const getProvidersMock = vi.hoisted(() => vi.fn())
const searchMock = vi.hoisted(() => vi.fn())
const downloadMock = vi.hoisted(() => vi.fn())
const getSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: {
        getProviders: getProvidersMock,
        search: searchMock,
        download: downloadMock
    }
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
    overrides: Partial<ISubtitleProviderManifest> = {}
): ISubtitleProviderManifest => ({
    provider: key,
    displayName: key.toUpperCase(),
    description: null,
    requiresAuth: false,
    credentialsConfigured: false,
    settings: [],
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

const outcome = (cacheKey: string, candidates: ISubtitleCandidate[]) => ({
    cacheKey,
    candidates,
    sourceOutcomes: []
})

const downloadResponse = (
    overrides: Partial<ISubtitleDownloadResponse> = {}
): ISubtitleDownloadResponse => ({
    historyId: 1,
    filePath: '/media/dune/dune.zh.srt',
    result: 'Downloaded',
    error: null,
    translationRequestId: null,
    translationDuplicate: false,
    ...overrides
})

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
    } = {}
): Promise<MountedModal> {
    const pinia = createPinia()
    const wrapper = mount(SubtitleSearchModal, {
        props: {
            mediaType: 'movie',
            mediaId: 7,
            title: 'Dune: Part Two',
            ...props
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
        await mountModal()

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
        const { toast, wrapper } = await mountModal()

        await clickButton(en.subSearch.downloadAndTranslate)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadedAndQueued.replace('{file}', '/media/dune/dune.zh.srt')
        )
        expect(toast.toasts[0].type).toBe('success')
        expect(wrapper.emitted('downloaded')).toEqual([['/media/dune/dune.zh.srt']])
    })

    it('toasts plain saved when no translation was queued', async () => {
        downloadMock.mockResolvedValue(downloadResponse())
        const { toast } = await mountModal()

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloaded.replace('{file}', '/media/dune/dune.zh.srt')
        )
    })

    it('flags a duplicate translation request', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ translationRequestId: 9, translationDuplicate: true })
        )
        const { toast } = await mountModal()

        await clickButton(en.subSearch.downloadAndTranslate)
        expect(toast.toasts[0].message).toBe(en.subSearch.translationDuplicate)
        expect(toast.toasts[0].type).toBe('info')
    })

    it('surfaces the server result and error when the file did not land', async () => {
        downloadMock.mockResolvedValue(
            downloadResponse({ filePath: '', result: 'Skipped', error: 'target already covered' })
        )
        const { toast } = await mountModal()

        await clickButton(en.subSearch.downloadOnly)
        expect(toast.toasts[0].message).toBe(
            en.subSearch.downloadRejected
                .replace('{result}', 'Skipped')
                .replace('{error}', 'target already covered')
        )
        expect(toast.toasts[0].type).toBe('error')
    })

    it('extracts the server message when the download request throws', async () => {
        downloadMock.mockRejectedValue({
            isAxiosError: true,
            message: 'Network Error',
            response: { data: { message: 'the search result expired' } }
        })
        const { toast } = await mountModal()

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
        await mountModal()

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
        await mountModal()

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
