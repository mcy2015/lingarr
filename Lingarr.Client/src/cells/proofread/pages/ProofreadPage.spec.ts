// @vitest-environment happy-dom
// Page-level keyboard-flow glue for the proofread workbench. The raw
// j/k/enter/e mapping lives in useProofreadKeyboard (covered by its own
// spec); what is pinned here are the page semantics on top of it: the
// edit-mode guard, J/K clamping, approve stepping, the draft round-trip
// and the topbar CTA lifecycle.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import services from '@/services'
import ProofreadPage from '@/cells/proofread/pages/ProofreadPage.vue'
import en from '@/i18n/locales/en.json'
import { useToastStore } from '@/cells/app/stores/toast'
import { useUiStore } from '@/cells/app/stores/ui'
import {
    ISubtitleLineComparison,
    ITranslationGlossary,
    ITranslationRequestDetail,
    MEDIA_TYPE,
    PROOFREAD_LINE_ORIGIN,
    TRANSLATION_SOURCE,
    TRANSLATION_STATUS
} from '@/types'

const glossaryGetAll = vi.hoisted(() => vi.fn())

vi.mock('@/services', () => ({
    default: {
        translationRequest: {
            get: vi.fn(),
            requests: vi.fn(),
            statusCounts: vi.fn(),
            applyProofreadLine: vi.fn()
        },
        translate: { proofreadStatus: vi.fn() }
    }
}))

vi.mock('@/cells/app/api/glossaryService', () => ({
    glossaryService: () => ({ getAll: glossaryGetAll })
}))

const getMock = vi.mocked(services.translationRequest.get)
const glossaryMock = vi.mocked(glossaryGetAll)
const applyProofreadLineMock = vi.mocked(services.translationRequest.applyProofreadLine)

// ── fixtures ───────────────────────────────────────────────────────
// A fresh detail per test: applyDraft mutates line.target in place, so
// rows must not be shared between tests.
const makeLines = (): ISubtitleLineComparison[] => [
    { position: 1, source: 'Hello there.', target: '你好。' },
    { position: 2, source: 'General Kenobi!', target: '肯诺比将军!' },
    { position: 3, source: 'You are a bold one.', target: '你真大胆。' },
    { position: 4, source: 'Kill him!', target: '杀了他!' }
]

const makeDetail = (): ITranslationRequestDetail => ({
    id: 1,
    jobId: 'job-1',
    title: 'S01E01.srt',
    sourceLanguage: 'en',
    targetLanguage: 'zh',
    mediaType: MEDIA_TYPE.MOVIE,
    status: TRANSLATION_STATUS.COMPLETED,
    source: TRANSLATION_SOURCE.IMPORT,
    progress: 100,
    events: [],
    lines: makeLines()
})

// ── scaffold ───────────────────────────────────────────────────────
// flushPromises relies on setImmediate/setTimeout which the fake
// timers own — advancing by 0ms yields the event loop the same way
// and additionally drains the microtask queue.
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

// The keyboard flow listens on window. Dispatching there (target =
// window, not the edit input) mirrors keys arriving while focus sits
// outside the editor; events bubbled from the input itself are the
// editor's own keydown.enter binding.
const press = (key: string): void => {
    window.dispatchEvent(new KeyboardEvent('keydown', { key, cancelable: true }))
}

interface ProofreadVm {
    loading: boolean
    focusedPosition: number | null
    editingPosition: number | null
    draft: string
    approved: Set<number>
    lines: ISubtitleLineComparison[]
    approvedCount: number
}

interface MountedProofread {
    wrapper: VueWrapper
    vm: ProofreadVm
    uiStore: ReturnType<typeof useUiStore>
    toastStore: ReturnType<typeof useToastStore>
}

let activeWrapper: VueWrapper | null = null

async function mountProofreadPage(): Promise<MountedProofread> {
    const pinia = createPinia()

    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false,
        warnHtmlMessage: false
    })

    // the real route shape: /proofread/:id? with props: true — the page
    // is mounted directly with the same string prop the router passes
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/proofread/:id?', name: 'proofread', component: ProofreadPage, props: true },
            { path: '/queue', name: 'queue', component: { template: '<div />' } }
        ]
    })
    await router.push('/proofread/1')
    await router.isReady()

    const wrapper = mount(ProofreadPage, {
        props: { id: '1' },
        global: { plugins: [pinia, i18n, router] }
    })
    activeWrapper = wrapper
    await settle()

    return {
        wrapper,
        vm: wrapper.vm as unknown as ProofreadVm,
        uiStore: useUiStore(pinia),
        toastStore: useToastStore(pinia)
    }
}

describe('ProofreadPage', () => {
    beforeEach(() => {
        vi.useFakeTimers()
        vi.clearAllMocks()
        getMock.mockResolvedValue(makeDetail())
        glossaryMock.mockResolvedValue([])
        applyProofreadLineMock.mockResolvedValue('')
    })

    afterEach(() => {
        activeWrapper?.unmount()
        activeWrapper = null
        vi.useRealTimers()
    })

    it('loads the request detail and focuses the first line', async () => {
        const { wrapper, vm } = await mountProofreadPage()

        expect(getMock).toHaveBeenCalledTimes(1)
        expect(getMock).toHaveBeenCalledWith(1)
        expect(vm.loading).toBe(false)
        expect(vm.focusedPosition).toBe(1)
        expect(vm.editingPosition).toBeNull()
        expect(wrapper.findAll('.pline')).toHaveLength(4)
    })

    // ── edit-mode guard ────────────────────────────────────────────
    it('approves the focused line on global Enter when not editing', async () => {
        const { vm } = await mountProofreadPage()

        press('Enter')

        expect(vm.approved.has(1)).toBe(true)
        expect(vm.focusedPosition).toBe(2)
        // plain approval is client-side state only — nothing persists
        expect(applyProofreadLineMock).not.toHaveBeenCalled()
    })

    it('ignores global Enter while a line is being edited — the input owns the key', async () => {
        const { wrapper, vm } = await mountProofreadPage()

        press('e')
        await settle()
        expect(vm.editingPosition).toBe(1)
        expect(vm.draft).toBe('你好。')
        expect(wrapper.find('input').exists()).toBe(true)

        press('Enter') // dispatched on window, not on the edit input
        await settle()

        expect(vm.approved.has(1)).toBe(false)
        expect(vm.editingPosition).toBe(1)
        expect(applyProofreadLineMock).not.toHaveBeenCalled()
    })

    it('approves from the edit input without a service round-trip when the draft is unchanged', async () => {
        const { wrapper, vm } = await mountProofreadPage()

        press('e')
        await settle()

        await wrapper.find('input').trigger('keydown', { key: 'Enter' })
        await settle()

        expect(vm.approved.has(1)).toBe(true)
        expect(vm.editingPosition).toBeNull()
        expect(vm.focusedPosition).toBe(2)
        expect(applyProofreadLineMock).not.toHaveBeenCalled()
    })

    it('persists a changed draft through applyProofreadLine, then approves and closes', async () => {
        const { wrapper, vm } = await mountProofreadPage()

        press('e')
        await settle()

        await wrapper.find('input').setValue('你好,将军。')
        await wrapper.find('input').trigger('keydown', { key: 'Enter' })
        await settle()

        expect(applyProofreadLineMock).toHaveBeenCalledTimes(1)
        expect(applyProofreadLineMock).toHaveBeenCalledWith({
            id: 1,
            position: 1,
            target: '你好,将军。',
            origin: PROOFREAD_LINE_ORIGIN.MANUAL
        })
        expect(vm.lines[0].target).toBe('你好,将军。')
        expect(vm.approved.has(1)).toBe(true)
        expect(vm.editingPosition).toBeNull()
        expect(vm.focusedPosition).toBe(2)
    })

    // ── J/K clamping ───────────────────────────────────────────────
    it('clamps at the first line when K is pressed repeatedly', async () => {
        const { vm } = await mountProofreadPage()
        expect(vm.focusedPosition).toBe(1)

        press('k')
        press('k')
        press('k')
        expect(vm.focusedPosition).toBe(1)
    })

    it('clamps at the last line when J is pressed past the end', async () => {
        const { vm } = await mountProofreadPage()

        press('j')
        press('j')
        press('j')
        expect(vm.focusedPosition).toBe(4)

        press('j')
        press('j')
        expect(vm.focusedPosition).toBe(4)
    })

    it('falls back to the first line when K arrives with no line focused', async () => {
        // detail has landed but the glossary await is still pending, so
        // the mount coroutine has not focused a line yet — focus is null
        let releaseGlossary: (() => void) | undefined
        glossaryMock.mockImplementation(
            () =>
                new Promise<ITranslationGlossary[]>((resolve) => {
                    releaseGlossary = () => resolve([])
                })
        )

        const { vm } = await mountProofreadPage()
        expect(vm.loading).toBe(true)
        expect(vm.focusedPosition).toBeNull()

        press('k')
        expect(vm.focusedPosition).toBe(1)

        releaseGlossary?.()
        await settle()
        expect(vm.loading).toBe(false)
        expect(vm.focusedPosition).toBe(1)
    })

    // ── approve stepping ───────────────────────────────────────────
    it('steps focus to the next unapproved line after each Enter', async () => {
        const { vm } = await mountProofreadPage()

        press('Enter')
        expect(vm.approved.has(1)).toBe(true)
        expect(vm.focusedPosition).toBe(2)

        press('Enter')
        expect(vm.approved.has(2)).toBe(true)
        expect(vm.focusedPosition).toBe(3)
    })

    it('wraps to the first unapproved line when the tail is exhausted', async () => {
        const { vm } = await mountProofreadPage()

        press('j') // focus line 2 — line 1 stays unapproved behind us
        press('Enter') // approve 2 → focus 3
        press('Enter') // approve 3 → focus 4
        press('Enter') // approve 4 → nothing after → wrap to 1

        expect(vm.approvedCount).toBe(3)
        expect(vm.focusedPosition).toBe(1)
    })

    it('skips already-approved lines when stepping', async () => {
        const { vm } = await mountProofreadPage()

        press('Enter') // approve 1 → focus 2
        press('Enter') // approve 2 → focus 3
        press('Enter') // approve 3 → focus 4
        press('k')
        press('k')
        press('k') // back on line 1 (still approved)

        press('Enter') // re-approve 1 → first unapproved after it is 4
        expect(vm.focusedPosition).toBe(4)
        expect(vm.approvedCount).toBe(3)
    })

    it('stops moving focus once every line is approved', async () => {
        const { vm } = await mountProofreadPage()

        press('j')
        press('Enter') // approve 2 → focus 3
        press('Enter') // approve 3 → focus 4
        press('Enter') // approve 4 → wrap to 1
        press('Enter') // approve 1 → everything green, nothing left

        expect(vm.approvedCount).toBe(4)
        expect(vm.focusedPosition).toBe(1)

        press('Enter') // one more Enter on a fully approved list: stays put
        expect(vm.focusedPosition).toBe(1)
    })

    it('Enter is one-way approval: pressing it on an approved line never un-approves', async () => {
        const { vm } = await mountProofreadPage()

        press('Enter') // approve 1 → focus 2
        press('k') // back to line 1
        expect(vm.focusedPosition).toBe(1)

        press('Enter')
        expect(vm.approved.has(1)).toBe(true)
        expect(vm.approvedCount).toBe(1)
    })

    // ── topbar CTA ─────────────────────────────────────────────────
    it('registers the stash CTA on mount and pushes a success toast on handler', async () => {
        const { uiStore, toastStore } = await mountProofreadPage()

        expect(uiStore.cta).not.toBeNull()
        expect(uiStore.cta?.label).toBe(en.topbar.cta.stash)

        uiStore.cta?.handler()

        expect(toastStore.toasts).toHaveLength(1)
        expect(toastStore.toasts[0].type).toBe('success')
        expect(toastStore.toasts[0].message).toBe(en.proofread.stashed)
    })

    it('clears the topbar CTA and sub override on unmount', async () => {
        const { wrapper, uiStore } = await mountProofreadPage()

        expect(uiStore.cta).not.toBeNull()
        expect(uiStore.subOverride).not.toBe('') // live sub set once detail loads

        wrapper.unmount()

        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
    })
})
