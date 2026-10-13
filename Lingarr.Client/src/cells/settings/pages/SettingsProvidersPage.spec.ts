// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import SettingsProvidersPage from '@/cells/settings/pages/SettingsProvidersPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ITranslationServiceHealth, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const getHealthMock = vi.hoisted(() => vi.fn())
const testConnectionMock = vi.hoisted(() => vi.fn())
const listPluginsMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        getHealth: getHealthMock,
        testConnection: testConnectionMock
    })
}))

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

const openai: ITranslationServiceHealth = {
    provider: 'openai',
    displayName: 'OpenAI',
    configured: true,
    latencyMs: null,
    successRate: null,
    lastError: null,
    lastRequestAt: null,
    sampleCount: 0,
    monthlyCost: null
}

const libre: ITranslationServiceHealth = {
    ...openai,
    provider: 'libretranslate',
    displayName: 'LibreTranslate',
    configured: false
}

getHealthMock.mockResolvedValue([openai, libre])
// no templated providers by default; the entry spec opts in per test
listPluginsMock.mockResolvedValue([])
setSettingMock.mockResolvedValue(undefined)

async function mountPage(serviceType = 'openai'): Promise<{
    wrapper: VueWrapper
    router: ReturnType<typeof createRouter>
    pinia: ReturnType<typeof createPinia>
}> {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SERVICE_TYPE, serviceType)

    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            {
                path: '/request-template/:service',
                name: 'request-template',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(SettingsProvidersPage, {
        global: { plugins: [pinia, i18n, router] },
        shallow: true
    })
    await flushPromises()
    return { wrapper, router, pinia }
}

function testButton(wrapper: VueWrapper) {
    // the label swaps to the pending variant while the probe is in flight
    return wrapper.findAll('button').find((button) =>
        ['Test connection', '◐ Testing…'].includes(button.text())
    )
}

describe('SettingsProvidersPage · 测试连接 (P0-A)', () => {
    it('offers the probe only on configured provider cards', async () => {
        const { wrapper } = await mountPage()

        expect(testButton(wrapper)).toBeDefined()
        expect(wrapper.find('[data-test="openai"]').exists()).toBe(false)
    })

    it('shows the inline green result with status, latency and message on success', async () => {
        testConnectionMock.mockResolvedValue({
            ok: true,
            status: 200,
            latencyMs: 812.4,
            message: '"Hello" -> "你好"'
        })
        const { wrapper } = await mountPage()

        await testButton(wrapper)!.trigger('click')
        await flushPromises()

        expect(testConnectionMock).toHaveBeenCalledWith('openai')
        const result = wrapper.find('[data-test="openai"]')
        expect(result.exists()).toBe(true)
        expect(result.attributes('data-ok')).toBe('true')
        expect(result.text()).toContain('✓ 200 · 812 ms')
        expect(result.text()).toContain('"Hello" -> "你好"')
    })

    it('shows the inline red result with the provider failure on error', async () => {
        testConnectionMock.mockResolvedValue({
            ok: false,
            status: 429,
            latencyMs: 42,
            message: 'Retry limit reached after TooManyRequests.'
        })
        const { wrapper } = await mountPage()

        await testButton(wrapper)!.trigger('click')
        await flushPromises()

        const result = wrapper.find('[data-test="openai"]')
        expect(result.attributes('data-ok')).toBe('false')
        expect(result.text()).toContain('✗ 429')
        expect(result.text()).toContain('Retry limit reached')
    })

    it('surfaces a rejected request as a failed probe instead of throwing', async () => {
        testConnectionMock.mockRejectedValue(new Error('network unreachable'))
        const { wrapper } = await mountPage()

        await testButton(wrapper)!.trigger('click')
        await flushPromises()

        const result = wrapper.find('[data-test="openai"]')
        expect(result.attributes('data-ok')).toBe('false')
        expect(result.text()).toContain('network unreachable')
    })

    it('shows a pending state while the probe is in flight', async () => {
        let resolveProbe: (value: unknown) => void = () => {}
        testConnectionMock.mockReturnValue(
            new Promise((resolve) => {
                resolveProbe = resolve
            })
        )
        const { wrapper } = await mountPage()

        await testButton(wrapper)!.trigger('click')
        await flushPromises()

        const pending = wrapper.find('[data-test="openai"]')
        expect(pending.text()).toContain('Testing…')
        expect(testButton(wrapper)!.attributes('disabled')).toBeDefined()

        resolveProbe({ ok: true, status: 200, latencyMs: 5, message: '"Hello" -> "你好"' })
        await flushPromises()

        expect(wrapper.find('[data-test="openai"]').attributes('data-ok')).toBe('true')
    })
})

describe('SettingsProvidersPage · request template entry (screen-10)', () => {
    it('offers the template drill-down only on configured providers that support templates', async () => {
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: true
            },
            {
                provider: 'libretranslate',
                displayName: 'LibreTranslate',
                isBuiltIn: true,
                hasRequestTemplate: false
            }
        ])

        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="tpl-entry-openai"]').text()).toBe(
            en.settings.providers.requestTemplate
        )
        // not templated -> no entry, even though the card renders
        expect(wrapper.find('[data-test="tpl-entry-libretranslate"]').exists()).toBe(false)
    })

    it('routes to /request-template/<service> when the entry is clicked', async () => {
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: true
            }
        ])

        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="tpl-entry-openai"]').trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('request-template')
        expect(router.currentRoute.value.params.service).toBe('openai')
    })

    it('hides the entry when the plugin list cannot be loaded', async () => {
        listPluginsMock.mockRejectedValue(new Error('offline'))

        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="tpl-entry-openai"]').exists()).toBe(false)
    })
})

describe('SettingsProvidersPage · 设为默认(A1 截链修复)', () => {
    const deepl: ITranslationServiceHealth = {
        ...openai,
        provider: 'deepl',
        displayName: 'DeepL'
    }

    it('moves the provider to the front and writes the FULL chain back as JSON', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        const { wrapper, pinia } = await mountPage('["openai","deepl","ollama"]')

        await wrapper.find('[data-test="set-default-deepl"]').trigger('click')
        await flushPromises()

        // one write, and it carries the whole reordered chain — not a bare provider
        expect(setSettingMock).toHaveBeenCalledWith('service_type', '["deepl","openai","ollama"]')
        const settingStore = useSettingStore(pinia)
        expect(JSON.parse(settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string)).toEqual([
            'deepl',
            'openai',
            'ollama'
        ])
    })

    it('toasts the switch with the display name', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        const { wrapper, pinia } = await mountPage('["openai","deepl"]')

        await wrapper.find('[data-test="set-default-deepl"]').trigger('click')
        await flushPromises()

        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(
            en.settings.providers.defaultSwitched.replace('{service}', 'DeepL')
        )
    })

    it('does not rewrite when the provider is already the default', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        setSettingMock.mockClear()
        const { wrapper } = await mountPage('["openai","deepl"]')

        // openai is the default: its card shows no set-default button at all
        expect(wrapper.find('[data-test="set-default-openai"]').exists()).toBe(false)
        expect(setSettingMock).not.toHaveBeenCalled()
    })
})

describe('SettingsProvidersPage · ＋添加服务与编辑模态(A3)', () => {
    it('lists unconfigured built-ins and plugins as add candidates and appends to the chain', async () => {
        getHealthMock.mockResolvedValue([openai, libre]) // libretranslate unconfigured
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: true
            },
            {
                provider: 'libretranslate',
                displayName: 'LibreTranslate',
                isBuiltIn: true,
                hasRequestTemplate: false
            },
            {
                provider: 'ollama-plugin',
                displayName: 'Ollama Plugin',
                isBuiltIn: false,
                hasRequestTemplate: false
            }
        ])
        setSettingMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai"]')

        await wrapper.find('[data-test="add-service-card"] button').trigger('click')
        // configured + chained providers are not candidates
        expect(wrapper.find('[data-test="add-candidate-openai"]').exists()).toBe(false)
        // unconfigured built-in and plugins are
        expect(wrapper.find('[data-test="add-candidate-libretranslate"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="add-candidate-ollama-plugin"]').exists()).toBe(true)

        await wrapper.find('[data-test="add-candidate-libretranslate"]').trigger('click')
        await flushPromises()

        // appended to the tail of the JSON chain (never a bare string)
        expect(setSettingMock).toHaveBeenCalledWith('service_type', '["openai","libretranslate"]')
        const settingStore = useSettingStore(pinia)
        expect(settingStore.getSetting(SETTINGS.SERVICE_TYPE)).toBe('["openai","libretranslate"]')
    })

    it('hides the add card when every provider is already chained and configured', async () => {
        getHealthMock.mockResolvedValue([openai]) // single configured provider
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: true
            }
        ])
        const { wrapper } = await mountPage('["openai"]')

        expect(wrapper.find('[data-test="add-service-card"]').exists()).toBe(false)
    })

    it('shows the model line only when the service model or batch size is readable', async () => {
        getHealthMock.mockResolvedValue([openai])
        const { wrapper, pinia } = await mountPage('["openai"]')

        // nothing stored yet -> row hidden
        expect(wrapper.find('[data-test="model-openai"]').exists()).toBe(false)

        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.OPENAI_MODEL, 'gpt-4o-mini')
        settingStore.storeSetting(SETTINGS.USE_BATCH_TRANSLATION, 'true')
        settingStore.storeSetting(SETTINGS.MAX_BATCH_SIZE, '500')
        await flushPromises()

        const line = wrapper.find('[data-test="model-openai"]')
        expect(line.exists()).toBe(true)
        expect(line.text()).toContain('gpt-4o-mini')
        expect(line.text()).toContain('500')
    })
})
