// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { DOMWrapper, mount, VueWrapper } from '@vue/test-utils'
import TranslationAction from '@/components/common/TranslationAction.vue'
import useTranslationRequestStore from '@/store/translationRequest'
import { ITranslationRequest, TRANSLATION_ACTIONS, TRANSLATION_STATUS } from '@/types'

// The i18n instance below carries no messages, so every `t(key)` used as
// a button title renders the raw key — titles double as stable handles.
const TITLES = {
    pause: 'common.translationAction.pause',
    resumePaused: 'common.translationAction.resumePaused',
    resume: 'common.translationAction.resume',
    proofread: 'common.translationAction.proofread',
    retry: 'common.translationAction.retry'
} as const

type ActionHandler = (action: TRANSLATION_ACTIONS) => Promise<void>

// ── fixtures ──────────────────────────────────────────────────────
const makeItem = (overrides: Partial<ITranslationRequest> = {}): ITranslationRequest =>
    ({
        id: 7,
        jobId: 'job-7',
        title: 'Dune',
        sourceLanguage: 'EN',
        targetLanguage: 'ZH',
        mediaType: 'Movie',
        source: 'Library',
        status: TRANSLATION_STATUS.FAILED,
        progress: 0,
        ...overrides
    }) as unknown as ITranslationRequest

// ── scaffold ──────────────────────────────────────────────────────
// flushPromises relies on setImmediate/setTimeout which the fake
// timers own — advancing by 0ms yields the event loop the same way
// and additionally drains the microtask queue.
const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0)
}

interface MountedAction {
    wrapper: VueWrapper
    onAction: ActionHandler
    router: ReturnType<typeof createRouter>
}

async function mountAction(
    item: ITranslationRequest,
    options: { proofreadSupported?: boolean; onAction?: ActionHandler } = {}
): Promise<MountedAction> {
    const pinia = createPinia()
    const store = useTranslationRequestStore(pinia)
    store.proofreadSupported = options.proofreadSupported ?? false

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
            { path: '/', name: 'home', component: { template: '<div />' } },
            { path: '/queue/:id', name: 'queue-detail', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const onAction = options.onAction ?? vi.fn()
    const wrapper = mount(TranslationAction, {
        props: { item, onAction },
        global: { plugins: [pinia, i18n, router] },
        shallow: true
    })

    return { wrapper, onAction, router }
}

// ── button helpers ────────────────────────────────────────────────
// With `shallow: true` the Icon child renders as <icon-stub name="…">,
// so every button is identified by its icon and, where present, the
// i18n key used as its title.
interface ButtonSpec {
    icon: string
    title: string
}

const eyeButton: ButtonSpec = { icon: 'eyeOn', title: '' }
const retryButton: ButtonSpec = { icon: 'retry', title: TITLES.retry }
const removeButton: ButtonSpec = { icon: 'trash', title: '' }

const allButtons = (wrapper: VueWrapper): DOMWrapper<Element>[] => wrapper.findAll('button')

const findButton = (
    wrapper: VueWrapper,
    matches: (button: DOMWrapper<Element>) => boolean
): DOMWrapper<Element> => {
    const button = allButtons(wrapper).find(matches)
    if (!button) {
        throw new Error('expected action button is not rendered')
    }
    return button
}

const buttonByTitle = (wrapper: VueWrapper, title: string): DOMWrapper<Element> =>
    findButton(wrapper, (button) => button.attributes('title') === title)

const buttonByIcon = (wrapper: VueWrapper, icon: string): DOMWrapper<Element> =>
    findButton(wrapper, (button) => button.find(`icon-stub[name="${icon}"]`).exists())

const renderedButtons = (wrapper: VueWrapper): ButtonSpec[] =>
    allButtons(wrapper).map((button) => {
        const icon = button.find('icon-stub')
        return {
            icon: icon.exists() ? (icon.attributes('name') ?? '') : '',
            title: button.attributes('title') ?? ''
        }
    })

// ── action dispatch table ─────────────────────────────────────────
// The component reports actions through the onAction callback prop
// (not a Vue emit); each button must pass the matching enum value.
interface DispatchCase {
    name: string
    item: Partial<ITranslationRequest>
    proofreadSupported: boolean
    title?: string
    icon?: string
    expected: TRANSLATION_ACTIONS
}

const dispatchCases: DispatchCase[] = [
    {
        name: 'pause an in-progress request',
        item: { status: TRANSLATION_STATUS.INPROGRESS },
        proofreadSupported: false,
        title: TITLES.pause,
        expected: TRANSLATION_ACTIONS.PAUSE
    },
    {
        name: 'pause a queued request',
        item: { status: TRANSLATION_STATUS.PENDING },
        proofreadSupported: false,
        title: TITLES.pause,
        expected: TRANSLATION_ACTIONS.PAUSE
    },
    {
        name: 'resume a paused request',
        item: { status: TRANSLATION_STATUS.PAUSED },
        proofreadSupported: false,
        title: TITLES.resumePaused,
        expected: TRANSLATION_ACTIONS.RESUME_PAUSED
    },
    {
        name: 'resume a failed request',
        item: { status: TRANSLATION_STATUS.FAILED },
        proofreadSupported: false,
        title: TITLES.resume,
        expected: TRANSLATION_ACTIONS.RESUME
    },
    {
        name: 'retry a failed request',
        item: { status: TRANSLATION_STATUS.FAILED },
        proofreadSupported: false,
        title: TITLES.retry,
        expected: TRANSLATION_ACTIONS.RETRY
    },
    {
        name: 'remove a failed request',
        item: { status: TRANSLATION_STATUS.FAILED },
        proofreadSupported: false,
        icon: 'trash',
        expected: TRANSLATION_ACTIONS.REMOVE
    },
    {
        name: 'proofread a completed request',
        item: {
            status: TRANSLATION_STATUS.COMPLETED,
            translatedSubtitle: '/out/dune.zh.srt'
        },
        proofreadSupported: true,
        title: TITLES.proofread,
        expected: TRANSLATION_ACTIONS.PROOFREAD
    }
]

describe('TranslationAction', () => {
    beforeEach(() => {
        vi.useFakeTimers()
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    // ── visibility matrix ─────────────────────────────────────────
    it.each([TRANSLATION_STATUS.PENDING, TRANSLATION_STATUS.INPROGRESS])(
        'offers only pause for a %s request',
        async (status) => {
            const { wrapper } = await mountAction(makeItem({ status }))

            expect(renderedButtons(wrapper)).toEqual([
                eyeButton,
                { icon: 'pause', title: TITLES.pause }
            ])
        }
    )

    it('offers resume-paused for a paused request', async () => {
        const { wrapper } = await mountAction(makeItem({ status: TRANSLATION_STATUS.PAUSED }))

        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'start', title: TITLES.resumePaused }
        ])
    })

    it('offers proofread, retry and remove for a completed request with a translated subtitle', async () => {
        const { wrapper } = await mountAction(
            makeItem({
                status: TRANSLATION_STATUS.COMPLETED,
                progress: 100,
                translatedSubtitle: '/out/dune.zh.srt'
            }),
            { proofreadSupported: true }
        )

        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'search', title: TITLES.proofread },
            retryButton,
            removeButton
        ])
    })

    it('drops the proofread button when the subtitle is missing or proofreading is unsupported', async () => {
        const withoutSubtitle = await mountAction(
            makeItem({ status: TRANSLATION_STATUS.COMPLETED, progress: 100 }),
            { proofreadSupported: true }
        )
        expect(renderedButtons(withoutSubtitle.wrapper)).toEqual([
            eyeButton,
            retryButton,
            removeButton
        ])

        const unsupported = await mountAction(
            makeItem({
                status: TRANSLATION_STATUS.COMPLETED,
                progress: 100,
                translatedSubtitle: '/out/dune.zh.srt'
            }),
            { proofreadSupported: false }
        )
        expect(renderedButtons(unsupported.wrapper)).toEqual([eyeButton, retryButton, removeButton])
    })

    it.each([
        TRANSLATION_STATUS.FAILED,
        TRANSLATION_STATUS.CANCELLED,
        TRANSLATION_STATUS.INTERRUPTED
    ])('offers resume, retry and remove for a %s request', async (status) => {
        const { wrapper } = await mountAction(makeItem({ status }))

        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'start', title: TITLES.resume },
            retryButton,
            removeButton
        ])
    })

    // ── detail navigation ─────────────────────────────────────────
    it('routes to the request detail page when the eye button is clicked', async () => {
        const { wrapper, onAction, router } = await mountAction(makeItem({ id: 42 }))

        await buttonByIcon(wrapper, 'eyeOn').trigger('click')
        await settle()

        expect(router.currentRoute.value.name).toBe('queue-detail')
        expect(router.currentRoute.value.params.id).toBe('42')
        expect(onAction).not.toHaveBeenCalled()
    })

    // ── action dispatch ───────────────────────────────────────────
    it.each(dispatchCases)('dispatches the matching action: $name', async (testCase) => {
        const { wrapper, onAction } = await mountAction(makeItem(testCase.item), {
            proofreadSupported: testCase.proofreadSupported
        })

        const button = testCase.title
            ? buttonByTitle(wrapper, testCase.title)
            : buttonByIcon(wrapper, testCase.icon ?? 'trash')

        await button.trigger('click')

        expect(onAction).toHaveBeenCalledTimes(1)
        expect(onAction).toHaveBeenCalledWith(testCase.expected)
    })

    // ── loading lifecycle ─────────────────────────────────────────
    it('swaps the action buttons for a loader while an action is pending', async () => {
        let resolveAction: () => void = () => {}
        const onAction = vi.fn(
            () =>
                new Promise<void>((resolve) => {
                    resolveAction = resolve
                })
        )
        const { wrapper } = await mountAction(makeItem({ status: TRANSLATION_STATUS.FAILED }), {
            onAction
        })

        await buttonByTitle(wrapper, TITLES.retry).trigger('click')

        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(true)
        expect(renderedButtons(wrapper)).toEqual([eyeButton])

        resolveAction()
        await settle()

        // retry clears the loader by itself once the handler resolves
        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(false)
        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'start', title: TITLES.resume },
            retryButton,
            removeButton
        ])
    })

    it('restores the buttons when the action handler rejects', async () => {
        let rejectAction: (reason?: unknown) => void = () => {}
        const onAction = vi.fn(
            () =>
                new Promise<void>((_, reject) => {
                    rejectAction = reject
                })
        )
        const { wrapper } = await mountAction(makeItem({ status: TRANSLATION_STATUS.PAUSED }), {
            onAction
        })

        await buttonByTitle(wrapper, TITLES.resumePaused).trigger('click')
        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(true)

        rejectAction(new Error('action failed'))
        await settle()

        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(false)
        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'start', title: TITLES.resumePaused }
        ])
    })

    it('clears the loader when the item status changes while an action is pending', async () => {
        let resolveAction: () => void = () => {}
        const onAction = vi.fn(
            () =>
                new Promise<void>((resolve) => {
                    resolveAction = resolve
                })
        )
        const item = makeItem({ status: TRANSLATION_STATUS.INPROGRESS })
        const { wrapper } = await mountAction(item, { onAction })

        await buttonByTitle(wrapper, TITLES.pause).trigger('click')
        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(true)

        await wrapper.setProps({
            item: { ...item, status: TRANSLATION_STATUS.PAUSED, progress: 10 }
        })

        expect(wrapper.find('icon-stub[name="loaderCircle"]').exists()).toBe(false)
        expect(renderedButtons(wrapper)).toEqual([
            eyeButton,
            { icon: 'start', title: TITLES.resumePaused }
        ])

        resolveAction()
    })
})
