// @vitest-environment happy-dom
// FallbackOrderCard (screen-06 A / batch-2 A2): the single editor of the
// service_type JSON array — rows render in chain order, ↑↓/remove write the
// array back, and the langcap chips are derived read-only from
// GET /api/translate/languages/{serviceType}. Batch-2 ①A: the add entry is a
// checkbox confirm panel — candidates only tick locally, the chain is written
// once on「添加 N 项」and cancel writes nothing.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import FallbackOrderCard from '@/cells/settings/components/FallbackOrderCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ITranslationServiceHealth, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const getServiceLanguages = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        getServiceLanguages
    })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: vi.fn().mockResolvedValue(undefined),
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

beforeEach(() => {
    getServiceLanguages.mockReset()
})

const health = (provider: string, displayName: string, configured = true): ITranslationServiceHealth => ({
    provider,
    displayName,
    configured,
    latencyMs: null,
    successRate: null,
    lastError: null,
    lastRequestAt: null,
    sampleCount: 0,
    monthlyCost: null
})

async function mountCard(chain: string, healthRows: ITranslationServiceHealth[]) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SERVICE_TYPE, chain)
    if (!getServiceLanguages.getMockImplementation()) {
        getServiceLanguages.mockResolvedValue({
            serviceType: 'x',
            status: 'unrestricted',
            languages: []
        })
    }

    const wrapper = mount(FallbackOrderCard, {
        props: { health: healthRows },
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, settingStore, toastStore: useToastStore(pinia), pinia }
}

const chainOf = (store: ReturnType<typeof useSettingStore>): string[] =>
    JSON.parse(store.getSetting(SETTINGS.SERVICE_TYPE) as string)

describe('FallbackOrderCard · 行渲染', () => {
    it('renders one row per chain member with index, name and role chips', async () => {
        const { wrapper } = await mountCard(
            '["openai","deepl","ollama"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL'), health('ollama', 'Ollama')]
        )

        expect(wrapper.findAll('[data-test^="fb-row-"]')).toHaveLength(3)
        expect(wrapper.find('[data-test="fb-row-0"]').text()).toContain('OpenAI')
        expect(wrapper.find('[data-test="fb-role-0"]').text()).toBe(en.settings.fallback.primary)
        expect(wrapper.find('[data-test="fb-role-1"]').text()).toBe(en.settings.fallback.fallbackSlot)
        expect(wrapper.find('[data-test="fb-role-2"]').text()).toBe(en.settings.fallback.fallbackSlot)
    })

    it('keeps the first row pinned (↑ disabled) and the last row pinned (↓ disabled)', async () => {
        const { wrapper } = await mountCard(
            '["openai","deepl"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL')]
        )

        expect(wrapper.find('[data-test="fb-up-0"]').attributes('disabled')).toBeDefined()
        expect(wrapper.find('[data-test="fb-down-1"]').attributes('disabled')).toBeDefined()
        expect(wrapper.find('[data-test="fb-up-1"]').attributes('disabled')).toBeUndefined()
    })
})

describe('FallbackOrderCard · 重排写回', () => {
    it('swaps members and writes the reordered array back to service_type', async () => {
        const { wrapper, settingStore } = await mountCard(
            '["openai","deepl","ollama"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL'), health('ollama', 'Ollama')]
        )

        await wrapper.find('[data-test="fb-up-2"]').trigger('click')

        expect(chainOf(settingStore)).toEqual(['openai', 'ollama', 'deepl'])
    })

    it('writes the array back as a JSON string (never a bare provider)', async () => {
        const { wrapper, settingStore } = await mountCard(
            '["openai","deepl"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL')]
        )

        await wrapper.find('[data-test="fb-up-1"]').trigger('click')

        expect(settingStore.getSetting(SETTINGS.SERVICE_TYPE)).toBe('["deepl","openai"]')
    })
})

describe('FallbackOrderCard · 移除', () => {
    it('removes the member, keeps the rest and toasts', async () => {
        const { wrapper, settingStore, toastStore } = await mountCard(
            '["openai","deepl"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL')]
        )

        await wrapper.find('[data-test="fb-remove-1"]').trigger('click')

        expect(chainOf(settingStore)).toEqual(['openai'])
        expect(toastStore.toasts[0].message).toBe(en.settings.fallback.removed.replace('{service}', 'DeepL'))
    })

    it('refuses to remove the last chain member', async () => {
        const { wrapper, settingStore } = await mountCard('["openai"]', [health('openai', 'OpenAI')])

        expect(wrapper.find('[data-test="fb-remove-0"]').attributes('disabled')).toBeDefined()
        await wrapper.find('[data-test="fb-remove-0"]').trigger('click')
        expect(chainOf(settingStore)).toEqual(['openai'])
    })
})

describe('FallbackOrderCard · 添加(①A 勾选确认)', () => {
    it('expands a checkbox panel listing every off-chain service (configured or not); chained ones excluded', async () => {
        const { wrapper } = await mountCard(
            '["openai"]',
            [
                health('openai', 'OpenAI'),
                health('deepl', 'DeepL'),
                health('libretranslate', 'LibreTranslate', false)
            ]
        )

        expect(wrapper.find('[data-test="fb-add-panel"]').exists()).toBe(false)
        await wrapper.find('[data-test="fb-add"]').trigger('click')

        expect(wrapper.find('[data-test="fb-add-panel"]').exists()).toBe(true)
        // off-chain candidates — configured AND unconfigured
        expect(wrapper.find('[data-test="fb-add-candidate-deepl"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="fb-add-candidate-libretranslate"]').exists()).toBe(true)
        // the chained member is not a candidate
        expect(wrapper.find('[data-test="fb-add-candidate-openai"]').exists()).toBe(false)
    })

    it('clicking a candidate only toggles the checkbox — the chain is not written', async () => {
        const { wrapper, settingStore } = await mountCard(
            '["openai"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL'), health('ollama', 'Ollama')]
        )

        await wrapper.find('[data-test="fb-add"]').trigger('click')
        await wrapper.find('[data-test="fb-add-candidate-check-deepl"]').setValue(true)
        await wrapper.find('[data-test="fb-add-candidate-check-ollama"]').setValue(true)
        await wrapper.find('[data-test="fb-add-candidate-check-deepl"]').setValue(false)

        // selection is local only: one service picked, the chain untouched
        expect(chainOf(settingStore)).toEqual(['openai'])
        expect(wrapper.find('[data-test="fb-add-confirm"]').text()).toBe(
            en.settings.fallback.addConfirm.replace('{count}', '1')
        )
    })

    it('confirm appends every selected service to the chain tail in one JSON write and toasts the count', async () => {
        const { wrapper, settingStore, toastStore } = await mountCard(
            '["openai"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL'), health('ollama', 'Ollama')]
        )

        await wrapper.find('[data-test="fb-add"]').trigger('click')
        await wrapper.find('[data-test="fb-add-candidate-check-deepl"]').setValue(true)
        await wrapper.find('[data-test="fb-add-candidate-check-ollama"]').setValue(true)
        await wrapper.find('[data-test="fb-add-confirm"]').trigger('click')
        await flushPromises()

        expect(chainOf(settingStore)).toEqual(['openai', 'deepl', 'ollama'])
        expect(toastStore.toasts[0].message).toBe(
            en.settings.fallback.addedCount.replace('{count}', '2')
        )
        // panel closes after the write
        expect(wrapper.find('[data-test="fb-add-panel"]').exists()).toBe(false)
    })

    it('keeps the confirm button disabled at zero selection', async () => {
        const { wrapper, settingStore } = await mountCard(
            '["openai"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL')]
        )

        await wrapper.find('[data-test="fb-add"]').trigger('click')

        const confirm = wrapper.find('[data-test="fb-add-confirm"]')
        expect(confirm.attributes('disabled')).toBeDefined()
        expect(confirm.text()).toBe(en.settings.fallback.addConfirm.replace('{count}', '0'))

        await confirm.trigger('click')
        expect(chainOf(settingStore)).toEqual(['openai'])
    })

    it('cancel closes the panel without writing anything', async () => {
        const { wrapper, settingStore, toastStore } = await mountCard(
            '["openai"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL')]
        )

        await wrapper.find('[data-test="fb-add"]').trigger('click')
        await wrapper.find('[data-test="fb-add-candidate-check-deepl"]').setValue(true)
        await wrapper.find('[data-test="fb-add-cancel"]').trigger('click')

        expect(wrapper.find('[data-test="fb-add-panel"]').exists()).toBe(false)
        expect(chainOf(settingStore)).toEqual(['openai'])
        expect(toastStore.toasts).toHaveLength(0)

        // reopening starts from a clean selection
        await wrapper.find('[data-test="fb-add"]').trigger('click')
        expect(
            (wrapper.find('[data-test="fb-add-candidate-check-deepl"]').element as HTMLInputElement)
                .checked
        ).toBe(false)
    })

    it('toasts when every service is already on the chain instead of opening the panel', async () => {
        const { wrapper, settingStore, toastStore } = await mountCard(
            '["openai","libretranslate"]',
            [health('openai', 'OpenAI'), health('libretranslate', 'LibreTranslate', false)]
        )

        await wrapper.find('[data-test="fb-add"]').trigger('click')

        expect(wrapper.find('[data-test="fb-add-panel"]').exists()).toBe(false)
        expect(toastStore.toasts[0].message).toBe(en.settings.fallback.allChained)
        expect(toastStore.toasts[0].type).toBe('info')
        expect(chainOf(settingStore)).toEqual(['openai', 'libretranslate'])
    })
})

describe('FallbackOrderCard · langcap 派生(只读)', () => {
    it('derives the three chip states from the per-service language tables', async () => {
        getServiceLanguages.mockImplementation(async (serviceType: string) => {
            if (serviceType === 'openai') {
                return { serviceType, status: 'unrestricted', languages: [] }
            }
            if (serviceType === 'deepl') {
                return {
                    serviceType,
                    status: 'restricted',
                    languages: [
                        { code: 'en', name: 'English' },
                        { code: 'zh', name: 'Chinese' },
                        { code: 'ja', name: 'Japanese' }
                    ]
                }
            }
            return { serviceType, status: 'unknown', languages: [] }
        })

        const { wrapper } = await mountCard(
            '["openai","deepl","ollama"]',
            [health('openai', 'OpenAI'), health('deepl', 'DeepL'), health('ollama', 'Ollama')]
        )

        expect(wrapper.find('[data-test="fb-langcap-0"]').text()).toBe(en.settings.fallback.langcapUnrestricted)
        expect(wrapper.find('[data-test="fb-langcap-1"]').text()).toBe(en.settings.fallback.langcapAll)
        expect(wrapper.find('[data-test="fb-langcap-2"]').text()).toBe(en.settings.fallback.langcapUnknown)

        // restricted: title carries the verified-table hint plus first language codes
        const title = wrapper.find('[data-test="fb-langcap-1"]').attributes('title')
        expect(title).toContain(en.settings.fallback.langcapAllTitle)
        expect(title).toContain('en, zh, ja')
    })

    it('toasts derived-read-only instead of editing on chip click', async () => {
        const { wrapper, toastStore } = await mountCard('["openai"]', [health('openai', 'OpenAI')])

        await wrapper.find('[data-test="fb-langcap-0"]').trigger('click')

        expect(toastStore.toasts[0].message).toBe(en.settings.fallback.langcapReadonly)
    })
})
