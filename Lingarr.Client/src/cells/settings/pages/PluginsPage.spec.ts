// @vitest-environment happy-dom
// Plugins drill-down (screen-06 A drill): the pb-head carries the identity
// (back to the providers group + a mono count sub line built from the real
// plugin list) and the body renders one setlist row per external plugin.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import PluginsPage from '@/cells/settings/pages/PluginsPage.vue'

const listPluginsMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/pluginService', () => ({
    pluginService: () => ({ list: listPluginsMock })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettings: vi.fn().mockResolvedValue({})
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const PLUGINS = [
    {
        provider: 'lingarr-openrouter',
        displayName: 'OpenRouter',
        description: 'OpenAI-compatible aggregator',
        sourceFile: '/app/plugins/Lingarr.OpenRouter.dll',
        isBuiltIn: false
    },
    {
        provider: 'lingarr-novelai',
        displayName: 'NovelAI',
        description: null,
        sourceFile: null,
        isBuiltIn: false
    },
    {
        provider: 'openai',
        displayName: 'OpenAI',
        isBuiltIn: true
    }
]

async function mountPage() {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            {
                path: '/settings/providers',
                name: 'settings-providers',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(PluginsPage, {
        global: { plugins: [createPinia(), router, i18n] }
    })
    await flushPromises()
    return { wrapper, router }
}

beforeEach(() => {
    listPluginsMock.mockReset().mockResolvedValue(PLUGINS)
})

describe('PluginsPage · pb-head', () => {
    it('renders the head with the providers back label and the plugin count sub line', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(
            en.settings.menu.groups.providers
        )
        expect(wrapper.text()).toContain(en.settings.menu.plugins)
        // mono sub line counts only the external (non built-in) plugins
        expect(wrapper.find('.mono').text()).toContain(
            en.settings.plugins.subLine.replace('{count}', '2')
        )
    })

    it('navigates back to the providers group screen', async () => {
        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-providers')
    })
})

describe('PluginsPage · 插件列表', () => {
    it('renders one row per external plugin with description and mono source file', async () => {
        const { wrapper } = await mountPage()

        const rows = wrapper.findAll('[data-test^="plugin-row-"]')
        expect(rows).toHaveLength(2)
        expect(wrapper.find('[data-test="plugin-row-lingarr-openrouter"]').text()).toContain(
            'OpenRouter'
        )
        expect(wrapper.find('[data-test="plugin-row-lingarr-openrouter"]').text()).toContain(
            '/app/plugins/Lingarr.OpenRouter.dll'
        )
        expect(wrapper.find('[data-test="plugin-row-openai"]').exists()).toBe(false)
    })

    it('keeps the services-page credential link from the original card', async () => {
        const { wrapper, router } = await mountPage()

        const link = wrapper.find('[data-test="plugins-services-link"]')
        expect(link.text()).toBe(en.services.plugins.servicesLink)
        await link.trigger('click')
        await flushPromises()
        expect(router.currentRoute.value.name).toBe('settings-providers')
    })

    it('surfaces the load error state on a failed listing', async () => {
        listPluginsMock.mockRejectedValue(new Error('boom'))
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="plugins-error"]').text()).toContain(
            en.services.plugins.loadError
        )
    })
})
