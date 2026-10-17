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
const getManifestMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        getHealth: getHealthMock,
        testConnection: testConnectionMock,
        // full mounts render FallbackOrderCard, which probes language tables and
        // settles every rejection into the read-only "unknown" chip
        getServiceLanguages: vi.fn().mockRejectedValue(new Error('offline'))
    })
}))

vi.mock('@/cells/settings/services/pluginService', () => ({
    pluginService: () => ({ list: listPluginsMock, getManifest: getManifestMock })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({}),
        setEncryptedSetting: setEncryptedSettingMock,
        revealEncryptedSetting: vi.fn().mockResolvedValue('')
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
getManifestMock.mockResolvedValue({
    provider: 'openai',
    displayName: 'OpenAI',
    isBuiltIn: true,
    hasRequestTemplate: false,
    settings: []
})
setSettingMock.mockResolvedValue(undefined)
setEncryptedSettingMock.mockResolvedValue(undefined)

async function mountPage(
    serviceType = 'openai',
    { shallow = true }: { shallow?: boolean } = {}
): Promise<{
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
        // shallow keeps the child cards stubbed; the delete-flow specs need the
        // teleported confirm modal, which only renders on a full mount
        shallow
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
        // unconfigured and off-chain: no card at all under the display rule
        expect(wrapper.find('[data-test="card-head-libretranslate"]').exists()).toBe(false)
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

        const { wrapper } = await mountPage('["openai","libretranslate"]')

        expect(wrapper.find('[data-test="tpl-entry-openai"]').text()).toBe(
            en.settings.providers.requestTemplate
        )
        // the chained card renders, but without template support there is no entry
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

describe('SettingsProvidersPage · 卡片展示规则(configured || 在链)', () => {
    const bing: ITranslationServiceHealth = {
        ...openai,
        provider: 'bing',
        displayName: 'Bing'
    }

    it('hides unconfigured off-chain services from the grid while keeping them addable', async () => {
        getHealthMock.mockResolvedValue([openai, libre]) // libretranslate unconfigured
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: false
            },
            {
                provider: 'libretranslate',
                displayName: 'LibreTranslate',
                isBuiltIn: true,
                hasRequestTemplate: false
            }
        ])
        const { wrapper } = await mountPage('["openai"]')

        expect(wrapper.find('[data-test="card-head-openai"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="card-head-libretranslate"]').exists()).toBe(false)

        await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
        expect(wrapper.find('[data-test="add-candidate-libretranslate"]').exists()).toBe(true)
    })

    it('keeps the grid non-empty when the chain is empty: configured (config-free) services stay visible', async () => {
        getHealthMock.mockResolvedValue([openai, libre])
        const { wrapper } = await mountPage('[]')

        expect(wrapper.find('[data-test="card-head-openai"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="card-head-libretranslate"]').exists()).toBe(false)
    })

    it('renders a configured off-chain (config-free) card WITH a delete button (②A), then chains it via the add menu', async () => {
        getHealthMock.mockResolvedValue([openai, bing]) // both configured
        listPluginsMock.mockResolvedValue([
            {
                provider: 'openai',
                displayName: 'OpenAI',
                isBuiltIn: true,
                hasRequestTemplate: false
            },
            {
                provider: 'bing',
                displayName: 'Bing',
                isBuiltIn: true,
                hasRequestTemplate: false
            }
        ])
        setSettingMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai"]')

        // configured -> visible; off-chain -> delete button is still there (②A)
        expect(wrapper.find('[data-test="card-head-bing"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="delete-bing"]').exists()).toBe(true)

        await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
        await wrapper.find('[data-test="add-candidate-bing"]').trigger('click')
        await flushPromises()

        // config-free service joined the chain: array written back, card stays
        expect(useSettingStore(pinia).getSetting(SETTINGS.SERVICE_TYPE)).toBe('["openai","bing"]')
        expect(wrapper.find('[data-test="delete-bing"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="card-head-bing"]').exists()).toBe(true)
    })
})

describe('SettingsProvidersPage · ＋添加服务与编辑模态(A3)', () => {
    it('lists every off-chain service (built-in + plugin, configured or not) as add candidates and appends to the chain', async () => {
        const bing: ITranslationServiceHealth = {
            ...openai,
            provider: 'bing',
            displayName: 'Bing'
        }
        getHealthMock.mockResolvedValue([openai, libre, bing]) // libretranslate unconfigured, bing configured
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
                provider: 'bing',
                displayName: 'Bing',
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

        await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
        // chained providers are not candidates — configured or not
        expect(wrapper.find('[data-test="add-candidate-openai"]').exists()).toBe(false)
        // off-chain ones are: unconfigured built-in, configured (config-free) built-in, plugin
        expect(wrapper.find('[data-test="add-candidate-libretranslate"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="add-candidate-bing"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="add-candidate-ollama-plugin"]').exists()).toBe(true)

        await wrapper.find('[data-test="add-candidate-libretranslate"]').trigger('click')
        await flushPromises()

        // appended to the tail of the JSON chain (never a bare string)
        expect(setSettingMock).toHaveBeenCalledWith('service_type', '["openai","libretranslate"]')
        const settingStore = useSettingStore(pinia)
        expect(settingStore.getSetting(SETTINGS.SERVICE_TYPE)).toBe('["openai","libretranslate"]')
    })

    it('hides the add card when every service is already on the chain', async () => {
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

    it('opens the candidate menu fixed to the viewport with a clamped, scrollable height (UI 批一 · 4)', async () => {
        // 8 candidates — the count that made the old absolute/bottom-full menu
        // unusable when it poked out above the settings scroll container
        getHealthMock.mockResolvedValue([openai])
        listPluginsMock.mockResolvedValue([
            { provider: 'openai', displayName: 'OpenAI', isBuiltIn: true, hasRequestTemplate: false },
            ...Array.from({ length: 8 }, (_, index) => ({
                provider: `plugin-${index}`,
                displayName: `Plugin ${index}`,
                isBuiltIn: false,
                hasRequestTemplate: false
            }))
        ])
        const { wrapper } = await mountPage('["openai"]')

        await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
        await flushPromises()

        const menu = wrapper.find('[data-test="add-service-menu"]')
        expect(menu.exists()).toBe(true)
        // fixed positioning escapes the scroll container's overflow clipping
        expect(menu.classes()).toContain('fixed')
        // long candidate lists scroll inside the menu
        expect(menu.classes()).toContain('overflow-y-auto')
        const style = menu.attributes('style') ?? ''
        // happy-dom (1024×768, anchor rect 0) → opens downward, height capped at 320px
        expect(style).toContain('max-height: 320px')
        expect(style).toContain('top:')
        // left is clamped inside the viewport, never negative
        const left = Number.parseInt(/left:\s*(\d+)px/.exec(style)?.[1] ?? '-1', 10)
        expect(left).toBeGreaterThanOrEqual(0)
        // every candidate lives inside the scrollable menu — all reachable
        expect(menu.findAll('[data-test^="add-candidate-"]')).toHaveLength(8)
    })

    it('shrinks the menu to the viewport height instead of overflowing it', async () => {
        getHealthMock.mockResolvedValue([openai])
        listPluginsMock.mockResolvedValue([
            { provider: 'openai', displayName: 'OpenAI', isBuiltIn: true, hasRequestTemplate: false },
            { provider: 'plugin-a', displayName: 'Plugin A', isBuiltIn: false, hasRequestTemplate: false }
        ])
        const originalHeight = window.innerHeight
        window.innerHeight = 160
        try {
            const { wrapper } = await mountPage('["openai"]')

            await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
            await flushPromises()

            const style = wrapper.find('[data-test="add-service-menu"]').attributes('style') ?? ''
            // 160px viewport - 6px gap = 154px of room below the (zero-rect) anchor
            expect(style).toContain('max-height: 154px')
        } finally {
            window.innerHeight = originalHeight
        }
    })

    it('closes the candidate menu on an outside click', async () => {
        getHealthMock.mockResolvedValue([openai])
        listPluginsMock.mockResolvedValue([
            { provider: 'openai', displayName: 'OpenAI', isBuiltIn: true, hasRequestTemplate: false },
            { provider: 'plugin-a', displayName: 'Plugin A', isBuiltIn: false, hasRequestTemplate: false }
        ])
        const { wrapper } = await mountPage('["openai"]')

        await wrapper.find('[data-test="add-service-toggle"]').trigger('click')
        expect(wrapper.find('[data-test="add-service-menu"]').exists()).toBe(true)

        window.document.body.click()
        await flushPromises()

        expect(wrapper.find('[data-test="add-service-menu"]').exists()).toBe(false)
    })

    it('fills the meta slot with model · batch info once readable, omitting the line before that (E7)', async () => {
        getHealthMock.mockResolvedValue([openai])
        const { wrapper, pinia } = await mountPage('["openai"]')

        // configured but nothing stored yet -> the whole mono line is absent
        // instead of an orphan dash
        const meta = wrapper.find('[data-test="card-meta-openai"]')
        expect(meta.exists()).toBe(true)
        expect(meta.find('.mono').exists()).toBe(false)
        expect(meta.text()).not.toContain('—')

        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.OPENAI_MODEL, 'gpt-4o-mini')
        settingStore.storeSetting(SETTINGS.USE_BATCH_TRANSLATION, 'true')
        settingStore.storeSetting(SETTINGS.MAX_BATCH_SIZE, '500')
        await flushPromises()

        const line = wrapper.find('[data-test="card-meta-openai"] .mono')
        expect(line.exists()).toBe(true)
        expect(line.text()).toContain('gpt-4o-mini')
        expect(line.text()).toContain('500')
    })
})

describe('SettingsProvidersPage · pcard 骨架对齐', () => {
    it('gives every card the same four slots with ops pinned as the last block', async () => {
        getHealthMock.mockResolvedValue([openai, libre])
        // libretranslate is unconfigured but on the chain — that keeps its card rendered
        const { wrapper } = await mountPage('["openai","libretranslate"]')

        for (const provider of ['openai', 'libretranslate']) {
            const slots = wrapper
                .findAll('[data-test]')
                .map((node) => node.attributes('data-test') ?? '')
                .filter((name) => name.startsWith('card-') && name.endsWith(`-${provider}`))
            expect(slots).toEqual([
                `card-head-${provider}`,
                `card-meta-${provider}`,
                `card-usage-${provider}`,
                `card-ops-${provider}`
            ])
        }
    })

    it('renders the meta slot on unconfigured cards with the weak copy', async () => {
        getHealthMock.mockResolvedValue([openai, libre])
        // unconfigured card stays visible because it sits on the chain
        const { wrapper } = await mountPage('["openai","libretranslate"]')

        expect(wrapper.find('[data-test="card-meta-libretranslate"]').text()).toBe(
            en.settings.providers.notConfigured
        )
    })

    it('folds the LibreTranslate URL into the meta slot instead of a dedicated row', async () => {
        const libreConfigured = { ...libre, configured: true }
        getHealthMock.mockResolvedValue([openai, libreConfigured])
        const { wrapper, pinia } = await mountPage('["openai","libretranslate"]')

        useSettingStore(pinia).storeSetting(
            SETTINGS.LIBRETRANSLATE_URL,
            'http://libretranslate:5000'
        )
        await flushPromises()

        expect(wrapper.find('[data-test="card-meta-libretranslate"]').text()).toContain(
            'http://libretranslate:5000'
        )
    })
})

describe('SettingsProvidersPage · 用量行(批E E7)', () => {
    it('labels latency and success as since-restart scope while the month keeps its cost', async () => {
        getHealthMock.mockResolvedValue([
            {
                ...openai,
                monthlyCost: 1.234,
                monthlyRequests: 22,
                latencyMs: 812,
                successRate: 0.98
            }
        ])
        const { wrapper } = await mountPage()

        const usage = wrapper.find('[data-test="card-usage-openai"]')
        // the month slot shows the cost, not the request phrase
        expect(usage.text()).toContain(en.settings.providers.month)
        expect(usage.text()).toContain('$1.23')
        expect(usage.text()).not.toContain('requests this month')
        // latency/success carry the rolling-window scope label, one each
        expect(usage.text()).toContain('812 ms')
        expect(usage.text()).toContain('98%')
        const scopes = usage.text().split(en.settings.providers.sinceRestart).length - 1
        expect(scopes).toBe(2)
    })

    it('shows the request phrase when the rate is unknown but requests were made', async () => {
        getHealthMock.mockResolvedValue([
            {
                ...openai,
                monthlyCost: null,
                monthlyRequests: 22,
                latencyMs: 812,
                successRate: 0.98
            }
        ])
        const { wrapper } = await mountPage()

        const usage = wrapper.find('[data-test="card-usage-openai"]')
        expect(usage.text()).toContain(
            en.settings.providers.requestsThisMonth.replace('{count}', '22')
        )
        // the phrase carries its own month wording — no doubled label
        expect(usage.text()).not.toContain(en.settings.providers.month)
        expect(usage.text()).not.toContain('—')
    })

    it('keeps the honest dash when neither cost nor requests are known', async () => {
        getHealthMock.mockResolvedValue([openai]) // monthlyCost null, no monthlyRequests
        const { wrapper } = await mountPage()

        const usage = wrapper.find('[data-test="card-usage-openai"]')
        expect(usage.text()).toContain(en.settings.providers.month)
        expect(usage.text()).toContain('—')
        expect(usage.text()).not.toContain('requests this month')
    })

    it('treats zero monthly requests as no data (dash, not a zero phrase)', async () => {
        getHealthMock.mockResolvedValue([
            { ...openai, monthlyCost: null, monthlyRequests: 0 }
        ])
        const { wrapper } = await mountPage()

        const usage = wrapper.find('[data-test="card-usage-openai"]')
        expect(usage.text()).not.toContain('requests this month')
        expect(usage.text()).toContain('—')
    })
})

describe('SettingsProvidersPage · 删除服务', () => {
    const deepl: ITranslationServiceHealth = {
        ...openai,
        provider: 'deepl',
        displayName: 'DeepL'
    }

    it('offers delete on every visible card (②A) — chained and configured off-chain alike', async () => {
        getHealthMock.mockResolvedValue([openai, libre, deepl]) // deepl configured but off-chain
        const { wrapper } = await mountPage('["openai"]')

        expect(wrapper.find('[data-test="delete-openai"]').exists()).toBe(true)
        // configured yet off-chain: the card renders and now carries delete too
        expect(wrapper.find('[data-test="card-head-deepl"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="delete-deepl"]').exists()).toBe(true)
        // unconfigured and off-chain: no card at all — hence no delete button
        expect(wrapper.find('[data-test="delete-libretranslate"]').exists()).toBe(false)
    })

    it('delete on an off-chain card only toasts「未在回退链上」 — no modal, zero writes', async () => {
        getHealthMock.mockResolvedValue([openai, deepl]) // deepl configured but off-chain
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        getManifestMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai"]', { shallow: false })

        await wrapper.find('[data-test="delete-deepl"]').trigger('click')
        await flushPromises()

        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(
            en.settings.providers.deleteNotChained.replace('{service}', 'DeepL')
        )
        expect(toastStore.toasts[0].type).toBe('info')
        // no confirm modal, nothing written or cleared
        expect(document.querySelector('[data-test="provider-delete-modal"]')).toBeNull()
        expect(setSettingMock).not.toHaveBeenCalled()
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(getManifestMock).not.toHaveBeenCalled()
        expect(useSettingStore(pinia).getSetting(SETTINGS.SERVICE_TYPE)).toBe('["openai"]')
    })

    it('delete on a chained card still opens the confirm modal (既有流程不回归)', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        setSettingMock.mockClear()
        const { wrapper } = await mountPage('["openai","deepl"]', { shallow: false })

        await wrapper.find('[data-test="delete-deepl"]').trigger('click')
        await flushPromises()

        expect(document.querySelector('[data-test="provider-delete-modal"]')).not.toBeNull()

        // close the teleported modal again — later specs query the same body
        ;(document.querySelector('[data-test="provider-delete-cancel"]') as HTMLElement).click()
        await flushPromises()
        expect(document.querySelector('[data-test="provider-delete-modal"]')).toBeNull()
    })

    it('offers delete for an unconfigured service that still sits on the chain', async () => {
        getHealthMock.mockResolvedValue([openai, libre])
        const { wrapper } = await mountPage('["openai","libretranslate"]')

        expect(wrapper.find('[data-test="delete-libretranslate"]').exists()).toBe(true)
    })

    it('refuses to delete the only service on the chain and toasts the guard', async () => {
        getHealthMock.mockResolvedValue([openai])
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai"]')

        await wrapper.find('[data-test="delete-openai"]').trigger('click')
        await flushPromises()

        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(en.settings.providers.deleteLastGuard)
        expect(toastStore.toasts[0].type).toBe('error')
        // the guard fires before the modal: nothing opens, nothing is written
        expect(document.querySelector('[data-test="provider-delete-modal"]')).toBeNull()
        expect(setSettingMock).not.toHaveBeenCalled()
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
    })

    it('cancelling the confirm modal leaves the chain and the configuration untouched', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        getManifestMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai","deepl"]', { shallow: false })

        await wrapper.find('[data-test="delete-openai"]').trigger('click')
        await flushPromises()
        expect(document.querySelector('[data-test="provider-delete-modal"]')).not.toBeNull()

        ;(document.querySelector('[data-test="provider-delete-cancel"]') as HTMLElement).click()
        await flushPromises()

        expect(document.querySelector('[data-test="provider-delete-modal"]')).toBeNull()
        expect(setSettingMock).not.toHaveBeenCalled()
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        expect(getManifestMock).not.toHaveBeenCalled()
        expect(useSettingStore(pinia).getSetting(SETTINGS.SERVICE_TYPE)).toBe(
            '["openai","deepl"]'
        )
    })

    it('confirming on the default removes it from the chain, clears its settings and migrates the default', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        getManifestMock.mockResolvedValue({
            provider: 'openai',
            displayName: 'OpenAI',
            isBuiltIn: true,
            hasRequestTemplate: false,
            settings: [
                { key: 'openai_model', label: 'Model', type: 'Text', required: false },
                { key: 'openai_api_key', label: 'API key', type: 'Secret', required: true }
            ]
        })
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai","deepl"]', { shallow: false })
        getHealthMock.mockClear()
        getHealthMock.mockResolvedValue([deepl]) // after the deletion openai is gone

        await wrapper.find('[data-test="delete-openai"]').trigger('click')
        await flushPromises()
        ;(document.querySelector('[data-test="provider-delete-confirm"]') as HTMLElement).click()
        await flushPromises()

        // 1) chain removal — the remaining chain is written back as JSON
        expect(setSettingMock).toHaveBeenCalledWith('service_type', '["deepl"]')
        // 2) configuration cleared: plain field emptied, secret emptied via the encrypted API
        expect(setSettingMock).toHaveBeenCalledWith('openai_model', '')
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('openai_api_key', '')
        // 3) health refreshed (chain refreshes derivatively through the store)
        expect(getHealthMock).toHaveBeenCalledTimes(1)

        const settingStore = useSettingStore(pinia)
        expect(settingStore.getSetting(SETTINGS.SERVICE_TYPE)).toBe('["deepl"]')
        expect(settingStore.getSetting(SETTINGS.OPENAI_MODEL)).toBe('')
        expect(settingStore.isEncryptedSettingConfigured('openai_api_key' as never)).toBe(false)

        // default migrated: the toast names the removed service and the new head
        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(
            en.settings.providers.deleteDefaultMigrated
                .replace('{service}', 'OpenAI')
                .replace('{next}', 'DeepL')
        )
    })

    it('confirming on a non-default service keeps the chain head as default', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        getManifestMock.mockResolvedValue({
            provider: 'deepl',
            displayName: 'DeepL',
            isBuiltIn: true,
            hasRequestTemplate: false,
            settings: [
                { key: 'deepl_api_key', label: 'API key', type: 'Secret', required: true }
            ]
        })
        setSettingMock.mockClear()
        setEncryptedSettingMock.mockClear()
        const { wrapper, pinia } = await mountPage('["openai","deepl"]', { shallow: false })

        await wrapper.find('[data-test="delete-deepl"]').trigger('click')
        await flushPromises()
        ;(document.querySelector('[data-test="provider-delete-confirm"]') as HTMLElement).click()
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('service_type', '["openai"]')
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('deepl_api_key', '')
        expect(useSettingStore(pinia).getSetting(SETTINGS.SERVICE_TYPE)).toBe('["openai"]')

        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(
            en.settings.providers.deleteDone.replace('{service}', 'DeepL')
        )
    })
})

describe('SettingsProvidersPage · 设为默认 → 回退顺序卡实时同步(批次二 · 3)', () => {
    const deepl: ITranslationServiceHealth = {
        ...openai,
        provider: 'deepl',
        displayName: 'DeepL'
    }

    it('reorders the fallback card live on the same mounted page — no remount needed', async () => {
        getHealthMock.mockResolvedValue([openai, deepl])
        const { wrapper, pinia } = await mountPage('["openai","deepl"]', { shallow: false })

        // before: openai is the primary, both rows render on the same page
        expect(wrapper.find('[data-test="fb-row-0"]').text()).toContain('OpenAI')
        expect(wrapper.find('[data-test="fb-row-1"]').text()).toContain('DeepL')

        await wrapper.find('[data-test="set-default-deepl"]').trigger('click')
        await flushPromises()

        // the store carries the reordered chain…
        expect(useSettingStore(pinia).getSetting(SETTINGS.SERVICE_TYPE)).toBe(
            '["deepl","openai"]'
        )
        // …and the fallback card below the grid reflects it immediately
        expect(wrapper.find('[data-test="fb-row-0"]').text()).toContain('DeepL')
        expect(wrapper.find('[data-test="fb-row-1"]').text()).toContain('OpenAI')
    })

    it('keeps a third member in place while the new default jumps to the head', async () => {
        const ollama: ITranslationServiceHealth = {
            ...openai,
            provider: 'ollama',
            displayName: 'Ollama'
        }
        getHealthMock.mockResolvedValue([openai, deepl, ollama])
        const { wrapper } = await mountPage('["openai","deepl","ollama"]', { shallow: false })

        await wrapper.find('[data-test="set-default-deepl"]').trigger('click')
        await flushPromises()

        expect(wrapper.find('[data-test="fb-row-0"]').text()).toContain('DeepL')
        expect(wrapper.find('[data-test="fb-row-1"]').text()).toContain('OpenAI')
        expect(wrapper.find('[data-test="fb-row-2"]').text()).toContain('Ollama')
    })
})
