// @vitest-environment happy-dom
// P1-3 language policy card on the quality tab, prototype setlist layout:
// three rows — target languages as ok chips, source whitelist as a mono
// hint, chain language coverage as a derived read-only row computed from the
// per-service tables (mocked). Both editable rows open the comma-separated
// ISO-code modal which saves through the same store path the former
// SourceAndTarget component used.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import LanguagePolicyCard from '@/cells/settings/components/LanguagePolicyCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { ILanguage, IServiceLanguageResponse, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const getServiceLanguages = vi.hoisted(() => vi.fn())
const getLanguages = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/translateService', () => ({
    translateService: () => ({
        getServiceLanguages,
        getLanguages
    })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: vi.fn().mockResolvedValue(undefined),
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
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, settingStore }
}

describe('LanguagePolicyCard · 三行渲染', () => {
    it('renders target languages as ok chips and the source whitelist as mono hint', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([language('en'), language('ja'), language('zh')])

        const { wrapper } = await mountCard('openai', [language('en'), language('ja')], [language('zh')])

        const targets = wrapper.find('[data-test="lp-row-targets"]')
        expect(targets.exists()).toBe(true)
        expect(targets.find('[data-test="lp-target-chip-ZH"]').text()).toContain('ZH ✓')
        expect(targets.find('[data-test="lp-edit-targets"]').exists()).toBe(true)

        const sources = wrapper.find('[data-test="lp-row-sources"]')
        expect(sources.exists()).toBe(true)
        expect(sources.find('[data-test="lp-source-hint"]').text()).toBe('EN · JA')
        expect(sources.find('[data-test="lp-edit-sources"]').exists()).toBe(true)

        expect(wrapper.find('[data-test="lp-row-coverage"]').exists()).toBe(true)
    })

    it('shows the empty hint when no target language is configured', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([])

        const { wrapper } = await mountCard('openai', [language('en')], [])

        expect(wrapper.find('[data-test="lp-row-targets"]').text()).toContain(
            en.settings.languagePolicy.empty
        )
        expect(wrapper.find('[data-test="lp-source-hint"]').text()).toBe('EN')
    })
})

describe('LanguagePolicyCard · 覆盖行(派生只读)', () => {
    it('derives full and partial coverage rows from the mocked language tables', async () => {
        getServiceLanguages.mockImplementation(async (serviceType: string) => {
            if (serviceType === 'openai') return table('openai', 'unrestricted', [])
            if (serviceType === 'deepl') return table('deepl', 'restricted', ['en', 'zh'])
            if (serviceType === 'ollama') return table('ollama', 'unrestricted', [])
            return table(serviceType, 'restricted', ['en', 'fr'])
        })
        getLanguages.mockResolvedValue([])

        const { wrapper } = await mountCard(
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
        getLanguages.mockResolvedValue([])

        const { wrapper } = await mountCard('deepl', [language('ja')], [language('zh')])

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
        getLanguages.mockResolvedValue([])

        const { wrapper } = await mountCard('["openai","deepl"]', [language('en')], [language('zh')])

        const row = wrapper.find('[data-test="coverage-EN-ZH"]')
        expect(row.text()).toContain('1/2')
        expect(row.text()).toContain('1 unknown')
    })

    it('shows the chain-empty warning without a service_type setting', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([])

        const { wrapper } = await mountCard(null, [language('en')], [language('zh')])

        expect(wrapper.find('[data-test="coverage-empty"]').exists()).toBe(true)
        expect(wrapper.text()).toContain(en.settings.languagePolicy.chainEmpty)
        expect(getServiceLanguages).not.toHaveBeenCalled()
    })
})

describe('LanguagePolicyCard · 编辑模态(与 SourceAndTarget 同款写回)', () => {
    it('edits target languages: prefilled input, save writes the JSON setting and refreshes chips', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([{ code: 'zh', name: 'Chinese' }, { code: 'ja', name: 'Japanese' }])

        const { wrapper, settingStore } = await mountCard('openai', [language('en')], [language('zh')])

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')

        const modal = document.querySelector('[data-test="lp-modal"]')
        expect(modal).not.toBeNull()
        const input = modal!.querySelector('[data-test="lp-modal-input"]') as HTMLInputElement
        expect(input.value).toBe('zh')

        input.value = 'zh, ja'
        input.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(modal!.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).click()
        await flushPromises()

        // same path the old SourceAndTarget used: array of {code,name} in the store
        const saved = settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) as ILanguage[]
        expect(saved).toEqual([
            { code: 'zh', name: 'Chinese' },
            { code: 'ja', name: 'Japanese' }
        ])

        const chips = wrapper.find('[data-test="lp-row-targets"]')
        expect(chips.find('[data-test="lp-target-chip-ZH"]').exists()).toBe(true)
        expect(chips.find('[data-test="lp-target-chip-JA"]').exists()).toBe(true)
    })

    it('edits the source whitelist through the same modal', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([{ code: 'en', name: 'English' }])

        const { wrapper, settingStore } = await mountCard('openai', [language('en')], [language('zh')])

        await wrapper.find('[data-test="lp-edit-sources"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!
        const input = modal.querySelector('[data-test="lp-modal-input"]') as HTMLInputElement
        expect(input.value).toBe('en')

        input.value = 'en, ko'
        input.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).click()
        await flushPromises()

        const saved = settingStore.getSetting(SETTINGS.SOURCE_LANGUAGES) as ILanguage[]
        expect(saved).toEqual([
            { code: 'en', name: 'English' },
            { code: 'ko', name: 'ko' }
        ])
        expect(wrapper.find('[data-test="lp-source-hint"]').text()).toBe('EN · KO')
    })

    it('blocks an empty language list and cancels without writing', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([])

        const { wrapper, settingStore } = await mountCard('openai', [language('en')], [language('zh')])
        const before = settingStore.getSetting(SETTINGS.TARGET_LANGUAGES)

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!
        const input = modal.querySelector('[data-test="lp-modal-input"]') as HTMLInputElement

        input.value = '   '
        input.dispatchEvent(new Event('input'))
        await flushPromises()

        expect(modal.querySelector('[data-test="lp-modal-error"]')).not.toBeNull()
        expect((modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).disabled).toBe(true)

        ;(modal.querySelector('[data-test="lp-modal-cancel"]') as HTMLButtonElement).click()
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.TARGET_LANGUAGES)).toBe(before)
        expect(document.querySelector('[data-test="lp-modal"]')).toBeNull()
    })
})
