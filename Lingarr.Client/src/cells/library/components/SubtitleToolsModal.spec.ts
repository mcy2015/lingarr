// @vitest-environment happy-dom
// SubtitleToolsModal (B10) — the per-file action center of the subtitle
// card. Covers the five actions' full dispatch contracts: re-translate
// rides the translate store (mediaId + ISubtitle + source + target +
// mediaType on the wire), bilingual re-render rides the convert endpoint
// (request id + target form), timeline sync/restore ride the B05
// endpoints, variant conversion and manual shift ride the new B10
// endpoints — each with its success toast AND its failure toast through
// the ApiError normalization chain. Plus the honest capability gates:
// unsupported formats / missing owner / dual-language rows disable the
// affected action with the reason as the title.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { mount, type VueWrapper } from '@vue/test-utils'
import SubtitleToolsModal from '@/cells/library/components/SubtitleToolsModal.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import { ApiError } from '@/cells/app/api/apiError'
import en from '@/i18n/locales/en.json'
import type { ITranslationRequest } from '@/types'
import type { IMediaSubtitleFile } from '@/types/services'

const timelineSyncMock = vi.hoisted(() => vi.fn())
const timelineRestoreMock = vi.hoisted(() => vi.fn())
const shiftMock = vi.hoisted(() => vi.fn())
const variantMock = vi.hoisted(() => vi.fn())
const convertMock = vi.hoisted(() => vi.fn())
const translateSubtitleMock = vi.hoisted(() => vi.fn())
const getActiveTranslationsMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/library/services/subtitleService', () => ({
    subtitleService: () => ({
        collect: vi.fn(),
        mergeBilingual: vi.fn(),
        timelineSync: timelineSyncMock,
        timelineRestore: timelineRestoreMock,
        shiftSubtitle: shiftMock,
        convertVariant: variantMock
    })
}))

vi.mock('@/cells/app/api/translationRequestService', () => ({
    translationRequestService: () => ({
        convert: convertMock,
        getActiveTranslations: getActiveTranslationsMock
    })
}))

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        translateSubtitle: translateSubtitleMock,
        getLanguages: vi.fn(),
        bulkTranslate: vi.fn(),
        translateEmbeddedTrack: vi.fn()
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

/// The dialog teleports to document.body — unmount after each test and
/// clear the body so no dialog shadows the next test's selectors.
let activeWrapper: VueWrapper | null = null

beforeEach(() => {
    vi.clearAllMocks()
    getActiveTranslationsMock.mockResolvedValue([])
})

afterEach(() => {
    activeWrapper?.unmount()
    activeWrapper = null
    document.body.innerHTML = ''
})

/// All service continuations are microtasks; two macrotask ticks let the
/// action chain (dispatch → toast → emit) drain without fake timers.
async function flush(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0))
    await new Promise((resolve) => setTimeout(resolve, 0))
}

// ── fixtures ──────────────────────────────────────────────────────
const FILE: IMediaSubtitleFile = {
    fileName: 'Dune.2023.zh.ass',
    directory: '/media/movies/dune',
    languages: ['zh'],
    format: 'ass',
    bytes: 4096
}
const OWNER = { mediaType: 'Movie' as const, mediaId: 7 }
const TARGETS = [
    { code: 'en', name: 'English' },
    { code: 'ja', name: 'Japanese' }
]
const REQUEST = {
    id: 5,
    jobId: 'job-5',
    title: 'Dune: Part Two',
    sourceLanguage: 'en',
    targetLanguage: 'zh',
    translatedSubtitle: '/media/movies/dune/Dune.2023.zh.ass',
    isBilingual: false,
    mediaType: 'Movie',
    status: 'Completed',
    source: 'Library',
    progress: 100
} as unknown as ITranslationRequest

interface MountedModal {
    wrapper: VueWrapper
    toast: ReturnType<typeof useToastStore>
}

async function mountModal(
    props: Partial<{
        file: IMediaSubtitleFile
        owner: { mediaType: 'Movie' | 'Episode'; mediaId: number } | null
        targetLanguages: { code: string; name: string }[]
        request: ITranslationRequest | null
    }> = {}
): Promise<MountedModal> {
    const pinia = createPinia()
    const wrapper = mount(SubtitleToolsModal, {
        props: {
            file: FILE,
            owner: OWNER,
            targetLanguages: TARGETS,
            request: REQUEST,
            ...props
        },
        global: { plugins: [pinia, i18n] }
    })
    activeWrapper = wrapper
    await flush()
    return { wrapper, toast: useToastStore(pinia) }
}

const elementByTest = <T extends HTMLElement>(test: string): T => {
    const element = document.querySelector(`[data-test="${test}"]`) as T | null
    expect(element, `element [data-test="${test}"] should render`).toBeTruthy()
    return element!
}

async function clickAction(test: string): Promise<void> {
    elementByTest<HTMLButtonElement>(test).click()
    await flush()
}

const lastToast = (toast: MountedModal['toast']) =>
    toast.toasts[toast.toasts.length - 1]

describe('SubtitleToolsModal · 重新翻译', () => {
    it('dispatches through the translate store with the file as source and toasts the request id', async () => {
        translateSubtitleMock.mockResolvedValue({ jobId: 42 })
        const { wrapper, toast } = await mountModal()

        await clickAction('tools-retranslate-action')

        // wire contract: mediaId + the ISubtitle built from the row + source
        // language + the selected target + mediaType, verbatim
        expect(translateSubtitleMock).toHaveBeenCalledTimes(1)
        expect(translateSubtitleMock).toHaveBeenCalledWith(
            7,
            {
                path: '/media/movies/dune/Dune.2023.zh.ass',
                language: 'zh',
                fileName: 'Dune.2023.zh.ass',
                format: '.ass',
                caption: ''
            },
            'zh',
            TARGETS[0],
            'Movie'
        )

        expect(lastToast(toast)?.type).toBe('success')
        expect(lastToast(toast)?.message).toContain('#42')
        expect(wrapper.emitted('done')).toHaveLength(1)
    })

    it('the picked target chip is the wire target', async () => {
        translateSubtitleMock.mockResolvedValue({ jobId: 43 })
        await mountModal()

        elementByTest<HTMLButtonElement>('tools-retranslate-target-ja').click()
        await flush()
        await clickAction('tools-retranslate-action')

        expect(translateSubtitleMock).toHaveBeenCalledWith(
            expect.anything(),
            expect.anything(),
            expect.anything(),
            TARGETS[1],
            expect.anything()
        )
    })

    it('a rejected call surfaces the ApiError message in the error toast', async () => {
        translateSubtitleMock.mockRejectedValue(
            new ApiError({ message: 'retranslate boom', status: 400 })
        )
        const { toast } = await mountModal()

        await clickAction('tools-retranslate-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('retranslate boom')
    })

    it('dual-language rows disable the action with the reason', async () => {
        await mountModal({
            file: { ...FILE, fileName: 'Dune.2023.scjp.ass', languages: ['zh', 'ja'] }
        })

        const button = elementByTest<HTMLButtonElement>('tools-retranslate-action')
        expect(button.disabled).toBe(true)
        expect(button.title).toBe(en.mediaDetail.b10tools.retranslate.notTranslatable)
    })
})

describe('SubtitleToolsModal · 双语重渲染', () => {
    it('converts the matched request to the other form and toasts the outcome', async () => {
        convertMock.mockResolvedValue({
            success: true,
            skipped: false,
            isBilingual: true,
            message: 'Converted to bilingual'
        })
        const { wrapper, toast } = await mountModal()

        await clickAction('tools-rerender-action')

        // the fixture request is monolingual → the target form on the wire is bilingual
        expect(convertMock).toHaveBeenCalledWith(5, true)
        expect(lastToast(toast)?.type).toBe('success')
        expect(lastToast(toast)?.message).toContain(
            en.mediaDetail.b10tools.rerender.formBilingual
        )
        expect(wrapper.emitted('done')).toHaveLength(1)
    })

    it('a skipped conversion is an honest info toast, not a success', async () => {
        convertMock.mockResolvedValue({
            success: true,
            skipped: true,
            message: 'Already bilingual'
        })
        const { toast } = await mountModal()

        await clickAction('tools-rerender-action')

        expect(lastToast(toast)?.type).toBe('info')
        expect(lastToast(toast)?.message).toBe(en.mediaDetail.b10tools.rerender.noChangeToast)
    })

    it('a rejected conversion surfaces the ApiError message', async () => {
        convertMock.mockRejectedValue(
            new ApiError({ message: 'rerender boom', status: 400 })
        )
        const { toast } = await mountModal()

        await clickAction('tools-rerender-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('rerender boom')
    })

    it('no matched request disables the action with the reason', async () => {
        await mountModal({ request: null })

        const button = elementByTest<HTMLButtonElement>('tools-rerender-action')
        expect(button.disabled).toBe(true)
        expect(button.title).toBe(en.mediaDetail.b10tools.rerender.noRequestTitle)
        expect(convertMock).not.toHaveBeenCalled()
    })
})

describe('SubtitleToolsModal · 时间轴校正与还原', () => {
    it('sync dispatches the ownership triple and toasts the real correction numbers', async () => {
        timelineSyncMock.mockResolvedValue({
            status: 'corrected',
            reason: null,
            ratio: 1,
            offsetSeconds: -0.35,
            score: 0.8,
            cues: 120
        })
        const { wrapper, toast } = await mountModal()

        await clickAction('tools-timeline-action')

        expect(timelineSyncMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass'
        })
        expect(lastToast(toast)?.type).toBe('success')
        expect(lastToast(toast)?.message).toContain('120')
        expect(lastToast(toast)?.message).toContain('-350')
        expect(wrapper.emitted('done')).toHaveLength(1)

        // the corrected verdict proves a backup exists — restore appears
        expect(elementByTest<HTMLButtonElement>('tools-timeline-restore')).toBeTruthy()
    })

    it('a failed verdict is data: the reason rides the error toast', async () => {
        timelineSyncMock.mockResolvedValue({
            status: 'failed',
            reason: 'correlation score 0.040 is below the configured minimum 0.10',
            ratio: 1,
            offsetSeconds: 0,
            score: 0.04,
            cues: 90
        })
        const { toast } = await mountModal()

        await clickAction('tools-timeline-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('below the configured minimum')
    })

    it('a rejected sync surfaces the ApiError message', async () => {
        timelineSyncMock.mockRejectedValue(
            new ApiError({ message: 'timeline boom', status: 400 })
        )
        const { toast } = await mountModal()

        await clickAction('tools-timeline-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('timeline boom')
    })

    it('restore rides the B05 restore endpoint and reports honestly', async () => {
        timelineSyncMock.mockResolvedValue({
            status: 'already-corrected',
            reason: 'a previous correction backup exists',
            ratio: 1,
            offsetSeconds: 0,
            score: 0,
            cues: 0
        })
        timelineRestoreMock.mockResolvedValue({
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass'
        })
        const { wrapper, toast } = await mountModal()

        // the already-corrected verdict reveals the backup → restore entry
        await clickAction('tools-timeline-action')
        expect(lastToast(toast)?.type).toBe('info')

        await clickAction('tools-timeline-restore')

        expect(timelineRestoreMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass'
        })
        expect(lastToast(toast)?.type).toBe('success')
        expect(wrapper.emitted('done')).toHaveLength(1)
    })
})

describe('SubtitleToolsModal · 繁简转换', () => {
    it('converts through the B01 core with the picked mode and toasts the change', async () => {
        variantMock.mockResolvedValue({ changed: true, reason: 'converted to simplified' })
        const { wrapper, toast } = await mountModal()

        await clickAction('tools-variant-action')

        expect(variantMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass',
            mode: 'simplified'
        })
        expect(lastToast(toast)?.type).toBe('success')
        expect(lastToast(toast)?.message).toContain(
            en.mediaDetail.b10tools.variant.simplifiedLabel
        )
        expect(wrapper.emitted('done')).toHaveLength(1)
    })

    it('the traditional chip switches the wire mode', async () => {
        variantMock.mockResolvedValue({ changed: true, reason: null })
        await mountModal()

        elementByTest<HTMLButtonElement>('tools-variant-mode-traditional').click()
        await flush()
        await clickAction('tools-variant-action')

        expect(variantMock).toHaveBeenCalledWith(
            expect.objectContaining({ mode: 'traditional' })
        )
    })

    it('a no-change answer is 200-style info with the real reason, never fake success', async () => {
        variantMock.mockResolvedValue({
            changed: false,
            reason: 'already in the target variant'
        })
        const { wrapper, toast } = await mountModal()

        await clickAction('tools-variant-action')

        expect(lastToast(toast)?.type).toBe('info')
        expect(lastToast(toast)?.message).toContain('already in the target variant')
        expect(wrapper.emitted('done')).toBeUndefined()
    })

    it('a rejected conversion surfaces the ApiError message', async () => {
        variantMock.mockRejectedValue(new ApiError({ message: 'variant boom', status: 400 }))
        const { toast } = await mountModal()

        await clickAction('tools-variant-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('variant boom')
    })
})

describe('SubtitleToolsModal · 手动偏移', () => {
    async function setShift(value: string): Promise<void> {
        const input = elementByTest<HTMLInputElement>('tools-shift-input')
        input.value = value
        input.dispatchEvent(new Event('input'))
        await flush()
    }

    it('+500ms rides the shift endpoint with the exact offset and toasts the real counts', async () => {
        shiftMock.mockResolvedValue({
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass',
            offsetMs: 500,
            cues: 240,
            clampedCues: 2,
            lastEndSeconds: 3200
        })
        const { wrapper, toast } = await mountModal()

        await setShift('500')
        await clickAction('tools-shift-action')

        expect(shiftMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 7,
            subtitlePath: '/media/movies/dune/Dune.2023.zh.ass',
            offsetMs: 500
        })
        expect(lastToast(toast)?.type).toBe('success')
        expect(lastToast(toast)?.message).toContain('500 ms')
        expect(lastToast(toast)?.message).toContain('240 cues')
        expect(lastToast(toast)?.message).toContain('2 clamped')
        expect(wrapper.emitted('done')).toHaveLength(1)
    })

    it('negative offsets ride the wire as-is (cues move earlier)', async () => {
        shiftMock.mockResolvedValue({
            subtitlePath: 'x',
            offsetMs: -250,
            cues: 1,
            clampedCues: 0,
            lastEndSeconds: 1
        })
        await mountModal()

        await setShift('-250')
        await clickAction('tools-shift-action')

        expect(shiftMock).toHaveBeenCalledWith(expect.objectContaining({ offsetMs: -250 }))
    })

    it('a zero offset never leaves the client', async () => {
        const { toast } = await mountModal()

        await setShift('0')
        await clickAction('tools-shift-action')

        expect(shiftMock).not.toHaveBeenCalled()
        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toBe(en.mediaDetail.b10tools.shift.invalidToast)
    })

    it('a rejected shift surfaces the ApiError message', async () => {
        shiftMock.mockRejectedValue(new ApiError({ message: 'shift boom', status: 400 }))
        const { toast } = await mountModal()

        await setShift('500')
        await clickAction('tools-shift-action')

        expect(lastToast(toast)?.type).toBe('error')
        expect(lastToast(toast)?.message).toContain('shift boom')
    })
})

describe('SubtitleToolsModal · 能力门(置灰并给原因)', () => {
    it('rewrite-capable actions disable on unsupported formats with the format reason', async () => {
        await mountModal({ file: { ...FILE, fileName: 'Dune.2023.zh.sub', format: 'sub' } })

        for (const test of ['tools-timeline-action', 'tools-variant-action', 'tools-shift-action']) {
            const button = elementByTest<HTMLButtonElement>(test)
            expect(button.disabled, `${test} should disable`).toBe(true)
            expect(button.title).toBe(en.mediaDetail.b10tools.formatUnsupportedTitle)
        }
        // re-render addresses the translation request, not the path — it stays usable
        expect(elementByTest<HTMLButtonElement>('tools-rerender-action').disabled).toBe(false)
    })

    it('a null owner disables the path-addressed actions with the ownership reason', async () => {
        await mountModal({ owner: null })

        for (const test of [
            'tools-retranslate-action',
            'tools-timeline-action',
            'tools-variant-action',
            'tools-shift-action'
        ]) {
            const button = elementByTest<HTMLButtonElement>(test)
            expect(button.disabled, `${test} should disable`).toBe(true)
            expect(button.title).toBe(en.mediaDetail.b10tools.noOwnerTitle)
        }
    })

    it('a rerender-request without targets still renders the re-translate no-target reason', async () => {
        await mountModal({ targetLanguages: [] })

        const button = elementByTest<HTMLButtonElement>('tools-retranslate-action')
        expect(button.disabled).toBe(true)
        expect(button.title).toBe(en.mediaDetail.b10tools.retranslate.noTarget)
    })
})
