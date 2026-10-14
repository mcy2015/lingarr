// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import LogsPage from '@/cells/settings/pages/LogsPage.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { useUiStore } from '@/cells/app/stores/ui'
import { ILogEntry } from '@/types'

// A stand-in EventSource: the page only assigns onmessage/onerror and calls
// close(), so a plain object with those slots covers the SSE surface.
const stream = vi.hoisted(() => {
    const sources: { onmessage: ((event: { data: string }) => void) | null; onerror: ((error: unknown) => void) | null; close: () => void }[] = []
    return {
        sources,
        getStream: vi.fn(() => {
            const source = {
                onmessage: null as ((event: { data: string }) => void) | null,
                onerror: null as ((error: unknown) => void) | null,
                close: vi.fn()
            }
            sources.push(source)
            return source
        })
    }
})

vi.mock('@/cells/settings/services/logsService', () => ({
    logsService: () => ({ getStream: stream.getStream })
}))

const makeEntry = (logLevel: string, message: string): ILogEntry =>
    ({
        logLevel,
        message,
        timestamp: '2026-10-13T15:41:22Z',
        formattedSource: 'TranslationJob',
        category: 'Lingarr.Server.Modules.Translation.TranslationJob'
    }) as ILogEntry

const entries = (): ILogEntry[] => [
    makeEntry('Information', 'request #2417 batch 3/4'),
    makeEntry('Information', 'request #2418 created'),
    makeEntry('Warning', 'GeminiService 429 rate limited'),
    makeEntry('Error', 'TranslationJob #2409 failed')
]

async function mountPage(): Promise<{ wrapper: VueWrapper; uiStore: ReturnType<typeof useUiStore> }> {
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
            { path: '/logs', name: 'logs', component: LogsPage },
            { path: '/settings/system', name: 'settings-system', component: { template: '<div />' } }
        ]
    })
    await router.push('/logs')
    await router.isReady()

    const wrapper = mount(LogsPage, { global: { plugins: [pinia, i18n, router] } })

    // stream the fixture entries into the page
    stream.sources.at(-1)!.onmessage?.({ data: JSON.stringify(entries()[0]) })
    stream.sources.at(-1)!.onmessage?.({ data: JSON.stringify(entries()[1]) })
    stream.sources.at(-1)!.onmessage?.({ data: JSON.stringify(entries()[2]) })
    stream.sources.at(-1)!.onmessage?.({ data: JSON.stringify(entries()[3]) })
    await Promise.resolve()

    return { wrapper, uiStore: useUiStore(pinia) }
}

const chip = (wrapper: VueWrapper, value: string) =>
    wrapper.find(`[data-test="log-chip-${value}"]`)

describe('LogsPage (drill-down, prototype screen-11)', () => {
    it('roots on the shared DrillDownPage skeleton (dd-page)', async () => {
        const { wrapper } = await mountPage()

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount point
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
    })

    it('renders the pb-head with the mono stream line and the level chips carrying client-side counts', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain('System')
        expect(wrapper.find('.mono.text-secondary-content').text()).toContain('Ring buffer')
        expect(chip(wrapper, 'all').text()).toBe('All 4')
        expect(chip(wrapper, 'information').text()).toBe('INFO 2')
        expect(chip(wrapper, 'warning').text()).toBe('WARN 1')
        expect(chip(wrapper, 'error').text()).toBe('ERROR 1')
    })

    it('filters rows through the pressed chip (single select, aria-pressed)', async () => {
        const { wrapper } = await mountPage()

        expect(chip(wrapper, 'all').attributes('aria-pressed')).toBe('true')
        expect(wrapper.findAll('.log-entry')).toHaveLength(4)

        await chip(wrapper, 'error').trigger('click')

        expect(chip(wrapper, 'error').attributes('aria-pressed')).toBe('true')
        expect(chip(wrapper, 'all').attributes('aria-pressed')).toBe('false')
        const rows = wrapper.findAll('.log-entry')
        expect(rows).toHaveLength(1)
        expect(rows[0].text()).toContain('TranslationJob #2409 failed')
    })

    it('shows every streamed row with the three mono columns and warn/err tinting', async () => {
        const { wrapper } = await mountPage()
        const rows = wrapper.findAll('.log-entry')

        const grid = rows[3].find('.grid')
        expect(grid.classes()).toContain('grid-cols-[70px_46px_1fr]')
        expect(grid.find('time').text()).toMatch(/\d{2}:\d{2}:\d{2}/)
        expect(grid.find('span.font-semibold').text()).toBe('ERR')
        expect(grid.find('span.text-danger').exists()).toBe(true)
        // the source prefix keeps its data inside the message column
        expect(grid.text()).toContain('TranslationJob')
    })

    it('toggles auto-scroll through the pb-head switch', async () => {
        const { wrapper } = await mountPage()

        const toggle = wrapper.find('[role="switch"]')
        expect(toggle.attributes('aria-checked')).toBe('true')

        await toggle.trigger('click')
        expect(wrapper.find('[role="switch"]').attributes('aria-checked')).toBe('false')
    })

    it('clears the view from the pb-head secondary action', async () => {
        const { wrapper } = await mountPage()

        await wrapper.find('[data-test="log-clear"]').trigger('click')

        expect(wrapper.findAll('.log-entry')).toHaveLength(0)
        expect(wrapper.find('[data-test="log-list"]').text()).toContain(
            en.settings.logs.waiting
        )
    })

    it('registers the export CTA and closes the stream on unmount', async () => {
        const { wrapper, uiStore } = await mountPage()

        expect(uiStore.cta?.label).toBe(en.settings.logs.exportCta)
        expect(uiStore.subOverride).toBe(en.settings.logs.topbarSub)

        wrapper.unmount()

        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
        expect(stream.sources.at(-1)!.close).toHaveBeenCalled()
    })
})
