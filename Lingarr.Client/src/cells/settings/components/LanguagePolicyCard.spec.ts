// @vitest-environment happy-dom
// P1-3 language policy card on the quality tab, prototype setlist layout:
// three rows — target languages as ok chips, source whitelist as a mono
// hint, chain language coverage as a derived read-only row computed from the
// per-service tables (mocked). Both editable rows open the multi-select
// language picker modal (options = the culture table from GET
// /api/translate/languages; the filter input cannot produce out-of-table
// codes) which saves through the same store path the former SourceAndTarget
// component used.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
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

/// The edit modal teleports to document.body and test-utils does not unmount
/// wrappers automatically — a modal left open by a failing test would leak
/// into the next test's document.querySelector. Unmount after each test.
let activeWrapper: VueWrapper | null = null

afterEach(() => {
    activeWrapper?.unmount()
    activeWrapper = null
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
    activeWrapper = wrapper
    await flushPromises()
    return { wrapper, settingStore }
}

describe('LanguagePolicyCard · 三行渲染', () => {
    it('renders target languages as ok chips and the source whitelist as mono hint', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([language('en'), language('ja'), language('zh')])

        const { wrapper } = await mountCard(
            'openai',
            [language('en'), language('ja')],
            [language('zh')]
        )

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

        const { wrapper } = await mountCard(
            '["openai","deepl"]',
            [language('en')],
            [language('zh')]
        )

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

describe('LanguagePolicyCard · 编辑模态(多选选择器,与 SourceAndTarget 同款写回)', () => {
    it('edits target languages: prefilled chips, pick from culture table, save writes the JSON setting and refreshes chips', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([
            { code: 'zh', name: 'Chinese' },
            { code: 'ja', name: 'Japanese' }
        ])

        const { wrapper, settingStore } = await mountCard(
            'openai',
            [language('en')],
            [language('zh')]
        )

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')

        const modal = document.querySelector('[data-test="lp-modal"]')
        expect(modal).not.toBeNull()
        // existing value prefills as a chip (acceptance: tr/nl-style values回显)
        expect(modal!.querySelector('[data-test="language-chip-ZH"]')).not.toBeNull()

        // open the dropdown and pick Japanese from the culture table
        ;(modal!.querySelector('[data-test="language-select-trigger"]') as HTMLElement).click()
        await flushPromises()
        const jaOption = modal!.querySelector('[data-test="language-option-ja"]')
        expect(jaOption).not.toBeNull()
        expect(jaOption!.textContent).toContain('ja')
        expect(jaOption!.textContent).toContain('Japanese')
        ;(jaOption as HTMLElement).click()
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
        getLanguages.mockResolvedValue([
            { code: 'en', name: 'English' },
            { code: 'ko', name: 'Korean' }
        ])

        const { wrapper, settingStore } = await mountCard(
            'openai',
            [language('en')],
            [language('zh')]
        )

        await wrapper.find('[data-test="lp-edit-sources"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!
        expect(modal.querySelector('[data-test="language-chip-EN"]')).not.toBeNull()

        ;(modal.querySelector('[data-test="language-select-trigger"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="language-option-ko"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).click()
        await flushPromises()

        const saved = settingStore.getSetting(SETTINGS.SOURCE_LANGUAGES) as ILanguage[]
        expect(saved).toEqual([
            { code: 'en', name: 'English' },
            { code: 'ko', name: 'Korean' }
        ])
        expect(wrapper.find('[data-test="lp-source-hint"]').text()).toBe('EN · KO')
    })

    it('prefills existing values (tr/nl) as chips even when absent from the culture table', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([{ code: 'zh', name: 'Chinese' }])

        const { wrapper } = await mountCard(
            'openai',
            [language('en')],
            [language('tr'), language('nl')]
        )

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!

        expect(modal.querySelector('[data-test="language-chip-TR"]')).not.toBeNull()
        expect(modal.querySelector('[data-test="language-chip-NL"]')).not.toBeNull()
    })

    it('cannot submit out-of-table codes: the filter input only filters and save keeps the legal selection', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([
            { code: 'zh', name: 'Chinese' },
            { code: 'ja', name: 'Japanese' }
        ])

        const { wrapper, settingStore } = await mountCard(
            'openai',
            [language('en')],
            [language('zh')]
        )

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!
        ;(modal.querySelector('[data-test="language-select-trigger"]') as HTMLElement).click()
        await flushPromises()

        // typing an arbitrary code filters to nothing — it never becomes a value
        const filter = modal.querySelector(
            '[data-test="language-select-filter"]'
        ) as HTMLInputElement
        filter.value = 'xx-not-a-language'
        filter.dispatchEvent(new Event('input'))
        await flushPromises()
        expect(modal.querySelector('[data-test="language-select-no-results"]')).not.toBeNull()

        // clear the filter, add the one legal pick, then save
        ;(modal.querySelector('[data-test="language-select-clear-search"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="language-option-ja"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).click()
        await flushPromises()

        const saved = settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) as ILanguage[]
        expect(saved.map((item) => item.code)).toEqual(['zh', 'ja'])
    })

    it('blocks an empty language list and cancels without writing', async () => {
        getServiceLanguages.mockResolvedValue(table('openai', 'unrestricted', []))
        getLanguages.mockResolvedValue([])

        const { wrapper, settingStore } = await mountCard(
            'openai',
            [language('en')],
            [language('zh')]
        )
        const before = settingStore.getSetting(SETTINGS.TARGET_LANGUAGES)

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!

        // strip the last chip through its × button — selection becomes empty
        ;(modal.querySelector('[data-test="language-chip-remove-ZH"]') as HTMLElement).click()
        await flushPromises()

        expect(modal.querySelector('[data-test="lp-modal-error"]')).not.toBeNull()
        expect(
            (modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).disabled
        ).toBe(true)

        ;(modal.querySelector('[data-test="lp-modal-cancel"]') as HTMLButtonElement).click()
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.TARGET_LANGUAGES)).toBe(before)
        expect(document.querySelector('[data-test="lp-modal"]')).toBeNull()
    })

    it('recomputes chain coverage rows as soon as new targets are saved', async () => {
        getServiceLanguages.mockResolvedValue(table('deepl', 'restricted', ['en', 'zh']))
        getLanguages.mockResolvedValue([
            { code: 'zh', name: 'Chinese' },
            { code: 'fr', name: 'French' }
        ])

        const { wrapper } = await mountCard('deepl', [language('en')], [language('zh')])

        expect(wrapper.find('[data-test="coverage-EN-ZH"]').text()).toContain('1/1')
        expect(wrapper.find('[data-test="coverage-EN-FR"]').exists()).toBe(false)

        await wrapper.find('[data-test="lp-edit-targets"]').trigger('click')
        const modal = document.querySelector('[data-test="lp-modal"]')!
        ;(modal.querySelector('[data-test="language-select-trigger"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="language-option-fr"]') as HTMLElement).click()
        await flushPromises()
        ;(modal.querySelector('[data-test="lp-modal-save"]') as HTMLButtonElement).click()
        await flushPromises()

        // deepl's table lacks fr → the new pair appears as a zero-coverage warning
        expect(wrapper.find('[data-test="coverage-EN-ZH"]').text()).toContain('1/1')
        const frRow = wrapper.find('[data-test="coverage-EN-FR"]')
        expect(frRow.exists()).toBe(true)
        expect(frRow.text()).toContain('0/1')
    })
})
