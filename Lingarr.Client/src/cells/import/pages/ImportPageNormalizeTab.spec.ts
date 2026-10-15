// @vitest-environment happy-dom
// ImportPage (F3): the workbench hosts a third「归整」tab. The normalize
// panel itself is stubbed here — its own flow lives in NormalizeTab.spec.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import ImportPage from '@/cells/import/pages/ImportPage.vue'

vi.mock('@/cells/import/components/NormalizeTab.vue', () => ({
    default: { name: 'NormalizeTab', template: '<div data-test="normalize-tab-stub" />' }
}))

// The page prefetches the show list for the season tab; offline in tests is
// the handled path (the season tab stays usable without a binding).
vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({
        shows: vi.fn().mockRejectedValue(new Error('offline'))
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

async function mountPage(query: Record<string, string> = {}) {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [{ path: '/import', name: 'import', component: { template: '<div />' } }]
    })
    await router.push({ path: '/import', query })
    await router.isReady()

    const wrapper = mount(ImportPage, {
        global: { plugins: [createPinia(), router, i18n] }
    })
    await flushPromises()
    return wrapper
}

describe('ImportPage · normalize tab wiring', () => {
    it('offers the third tab and stays on free import by default', async () => {
        const wrapper = await mountPage()

        const labels = wrapper.findAll('[role="tab"]').map((tab) => tab.text())
        expect(labels).toEqual([
            en.import.tabs.free,
            en.import.tabs.season,
            en.import.tabs.normalize
        ])
        expect(wrapper.find('[data-test="normalize-tab-stub"]').exists()).toBe(false)
        // The free-import dropzone is the default panel.
        expect(wrapper.text()).toContain(en.import.dropzone.title)
    })

    it('deep-links to the normalize tab via ?tab=normalize', async () => {
        const wrapper = await mountPage({ tab: 'normalize' })

        expect(wrapper.find('[data-test="normalize-tab-stub"]').exists()).toBe(true)
        expect(wrapper.text()).not.toContain(en.import.dropzone.title)
    })

    it('switches to the normalize tab on click', async () => {
        const wrapper = await mountPage()

        await wrapper.findAll('[role="tab"]')[2].trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-test="normalize-tab-stub"]').exists()).toBe(true)
    })
})
