// @vitest-environment happy-dom
// P1-3 language policy card on the quality tab: renders the derived
// chain-language-coverage row from the store's chain/language settings and
// the per-service language tables (mocked). Editing stays in SourceAndTarget
// (stubbed here) — the card is the single mount point for it.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import LanguagePolicyCard from '@/cells/settings/components/LanguagePolicyCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { ILanguage, IServiceLanguageResponse, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const getServiceLanguages = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        getServiceLanguages
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const language = (code: string): ILanguage => ({ code, name: code })

const table = (
    serviceType: string,
    status: IServiceLanguageResponse['status'],
    codes: string[]
): IServiceLanguageResponse => ({
    serviceType,
    status,
    languages: codes.map((code) => ({ code, name: code }))
})

async function mountCard(chain: string | null, sources: ILanguage[], targets: ILanguage[]) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    if (chain !== null) {
        settingStore.storeSetting(SETTINGS.SERVICE_TYPE, chain)
    }
    settingStore.storeSetting(SETTINGS.SOURCE_LANGUAGES, sources)
    settingStore.storeSetting(SETTINGS.TARGET_LANGUAGES, targets)

    const wrapper = mount(LanguagePolicyCard, {
        global: { plugins: [pinia, i18n] },
        shallow: true
    })
    await flushPromises()
    return wrapper
}

describe('LanguagePolicyCard', () => {
    it('derives full and partial coverage rows from the mocked language tables', async () => {
        getServiceLanguages.mockImplementation(async (serviceType: string) => {
            if (serviceType === 'openai') return table('openai', 'unrestricted', [])
            if (serviceType === 'deepl') return table('deepl', 'restricted', ['en', 'zh'])
            if (serviceType === 'ollama') return table('ollama', 'unrestricted', [])
            return table(serviceType, 'restricted', ['en', 'fr'])
        })

        const wrapper = await mountCard(
            '["openai","deepl","ollama"]',
            [language('en'), language('ja')],
            [language('zh')]
        )

        const enRow = wrapper.find('[data-test="coverage-EN-ZH"]')
        expect(enRow.exists()).toBe(true)
        expect(enRow.text()).toContain('3/3')

        const jaRow = wrapper.find('[data-test="coverage-JA-ZH"]')
        // the AI-style services are unrestricted; only deepl's table lacks ja
        expect(jaRow.text()).toContain('2/3')
        expect(jaRow.text()).toContain('deepl')
    })

    it('flags a zero-coverage pair with the warning prefix', async () => {
        getServiceLanguages.mockResolvedValue(table('deepl', 'restricted', ['en', 'fr']))

        const wrapper = await mountCard('deepl', [language('ja')], [language('zh')])

        const row = wrapper.find('[data-test="coverage-JA-ZH"]')
        expect(row.text()).toContain('⚠')
        expect(row.text()).toContain('0/1')
    })

    it('surfaces unknown tables distinctly and renders them per pair', async () => {
        getServiceLanguages.mockImplementation(async (serviceType: string) =>
            serviceType === 'openai'
                ? table('openai', 'unrestricted', [])
                : table('deepl', 'unknown', [])
        )

        const wrapper = await mountCard('["openai","deepl"]', [language('en')], [language('zh')])

        const row = wrapper.find('[data-test="coverage-EN-ZH"]')
        expect(row.text()).toContain('1/2')
        expect(row.text()).toContain('1 unknown')
    })

    it('shows the chain-empty warning without a service_type setting', async () => {
        const wrapper = await mountCard(null, [language('en')], [language('zh')])

        expect(wrapper.find('[data-test="coverage-empty"]').exists()).toBe(true)
        expect(wrapper.text()).toContain(en.settings.languagePolicy.chainEmpty)
        expect(getServiceLanguages).not.toHaveBeenCalled()
    })
})
