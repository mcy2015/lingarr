// @vitest-environment happy-dom
// Multi-select language picker (refactored from the SourceAndTarget orphan):
// the panel lists the full culture table as 「code · name」, the top input is
// filter-only (code or name, case-insensitive) and can never produce an
// out-of-table code, selected values render as chips with individual ×
// removal, ESC / click-outside close, and the trigger carries the
// DropdownComponent-style aria conventions.
import { describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import { createI18n } from 'vue-i18n'
import { mount } from '@vue/test-utils'
import LanguageSelect from '@/cells/settings/components/LanguageSelect.vue'
import { ILanguage } from '@/types'
import en from '@/i18n/locales/en.json'

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const cultureTable: ILanguage[] = [
    { code: 'zh', name: 'Chinese' },
    { code: 'ja', name: 'Japanese' },
    { code: 'nl', name: 'Dutch' },
    { code: 'en-US', name: 'English (United States)' }
]

const mountSelect = (selected: ILanguage[] = []) =>
    mount(LanguageSelect, {
        props: { options: cultureTable, selected },
        global: { plugins: [i18n] }
    })

const openPanel = async (wrapper: ReturnType<typeof mountSelect>) => {
    await wrapper.find('[data-test="language-select-trigger"]').trigger('click')
}

/// v-show hides the panel with an inline display:none — happy-dom's computed
/// style ignores it, so isVisible() is unusable here; read the inline style.
const panelOpen = (wrapper: ReturnType<typeof mountSelect>): boolean =>
    !(wrapper.find('[data-test="language-select-panel"]').attributes('style') ?? '').includes(
        'display: none'
    )

describe('LanguageSelect · 渲染', () => {
    it('shows the placeholder when nothing is selected', () => {
        const wrapper = mountSelect()

        expect(wrapper.find('[data-test="language-select-trigger"]').text()).toContain(
            en.settings.languageSelect.placeholder
        )
    })

    it('renders one chip per preselected language (existing values like tr/nl prefill)', () => {
        const wrapper = mountSelect([
            { code: 'tr', name: 'tr' },
            { code: 'nl', name: 'Dutch' }
        ])

        expect(wrapper.find('[data-test="language-chip-TR"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="language-chip-NL"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="language-chip-TR"]').text()).toContain('TR')
    })

    it('exposes aria conventions on the trigger (haspopup/expanded/combobox)', async () => {
        const wrapper = mountSelect()
        const trigger = wrapper.find('[data-test="language-select-trigger"]')

        expect(trigger.attributes('role')).toBe('combobox')
        expect(trigger.attributes('aria-haspopup')).toBe('listbox')
        expect(trigger.attributes('aria-expanded')).toBe('false')

        await openPanel(wrapper)
        expect(trigger.attributes('aria-expanded')).toBe('true')
    })
})

describe('LanguageSelect · 选项与过滤', () => {
    it('lists the full culture table in 「code · name」 format', async () => {
        const wrapper = mountSelect()
        await openPanel(wrapper)

        const options = wrapper.findAll('[data-test^="language-option-"]')
        expect(options).toHaveLength(cultureTable.length)
        expect(wrapper.find('[data-test="language-option-zh"]').text()).toContain('zh')
        expect(wrapper.find('[data-test="language-option-zh"]').text()).toContain('Chinese')
        expect(wrapper.find('[data-test="language-option-en-US"]').text()).toContain('en-US')
    })

    it('filters by code and by name, case-insensitively', async () => {
        const wrapper = mountSelect()
        await openPanel(wrapper)
        const filter = wrapper.find('[data-test="language-select-filter"]')

        await filter.setValue('JAP')
        expect(wrapper.findAll('[data-test^="language-option-"]')).toHaveLength(1)
        expect(wrapper.find('[data-test="language-option-ja"]').exists()).toBe(true)

        await filter.setValue('dutch')
        expect(wrapper.findAll('[data-test^="language-option-"]')).toHaveLength(1)
        expect(wrapper.find('[data-test="language-option-nl"]').exists()).toBe(true)
    })

    it('shows the no-results state for unmatched input and commits nothing on Enter', async () => {
        const wrapper = mountSelect()
        await openPanel(wrapper)
        const filter = wrapper.find('[data-test="language-select-filter"]')

        await filter.setValue('xx-not-a-code')
        expect(wrapper.find('[data-test="language-select-no-results"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="language-select-no-results"]').text()).toBe(
            en.settings.languageSelect.noResults
        )

        // the filter input is filter-only: Enter must not turn free text into a value
        await filter.trigger('keydown.enter')
        expect(wrapper.emitted('update:selected')).toBeUndefined()
        expect(wrapper.findAll('[data-test^="language-chip-"]')).toHaveLength(0)
    })

    it('shows the no-options state when the culture table is empty', async () => {
        const wrapper = mount(LanguageSelect, {
            props: { options: [], selected: [] },
            global: { plugins: [i18n] }
        })
        await openPanel(wrapper)

        expect(wrapper.find('[data-test="language-select-no-options"]').text()).toBe(
            en.settings.languageSelect.noOptions
        )
    })
})

describe('LanguageSelect · 选中与移除', () => {
    it('adds an option to the selection and keeps the panel open for more picks', async () => {
        const wrapper = mountSelect()
        await openPanel(wrapper)

        // controlled component: the parent (v-model) applies each emit before
        // the next pick, so consecutive picks accumulate
        await wrapper.find('[data-test="language-option-ja"]').trigger('click')
        await wrapper.setProps({
            selected: wrapper.emitted<[ILanguage[]]>('update:selected')!.at(-1)![0]
        })
        await wrapper.find('[data-test="language-option-nl"]').trigger('click')

        const emitted = wrapper.emitted('update:selected')
        expect(emitted).toHaveLength(2)
        expect(emitted![0][0]).toEqual([{ code: 'ja', name: 'Japanese' }])
        expect(emitted![1][0]).toEqual([
            { code: 'ja', name: 'Japanese' },
            { code: 'nl', name: 'Dutch' }
        ])
        // multi-select: the panel does not auto-close after a pick
        expect(panelOpen(wrapper)).toBe(true)
    })

    it('toggles: clicking a selected option removes it from the emitted array', async () => {
        const wrapper = mountSelect([{ code: 'nl', name: 'Dutch' }])
        await openPanel(wrapper)

        expect(wrapper.find('[data-test="language-option-nl"]').attributes('aria-selected')).toBe(
            'true'
        )
        await wrapper.find('[data-test="language-option-nl"]').trigger('click')

        expect(wrapper.emitted('update:selected')![0][0]).toEqual([])
    })

    it('removes a single language via the chip × without opening the panel', async () => {
        const wrapper = mountSelect([
            { code: 'tr', name: 'tr' },
            { code: 'nl', name: 'Dutch' }
        ])

        await wrapper.find('[data-test="language-chip-remove-TR"]').trigger('click')

        expect(wrapper.emitted('update:selected')![0][0]).toEqual([{ code: 'nl', name: 'Dutch' }])
        // controlled component: the chip disappears once the parent applies the emit
        await wrapper.setProps({ selected: [{ code: 'nl', name: 'Dutch' }] })
        expect(wrapper.find('[data-test="language-chip-TR"]').exists()).toBe(false)
        expect(panelOpen(wrapper)).toBe(false)
    })
})

describe('LanguageSelect · 关闭与禁用', () => {
    it('closes on ESC from the filter input and on click outside', async () => {
        const wrapper = mountSelect()
        await openPanel(wrapper)
        expect(panelOpen(wrapper)).toBe(true)

        await wrapper.find('[data-test="language-select-filter"]').trigger('keydown.escape')
        expect(panelOpen(wrapper)).toBe(false)
        expect(
            wrapper.find('[data-test="language-select-trigger"]').attributes('aria-expanded')
        ).toBe('false')

        await openPanel(wrapper)
        expect(panelOpen(wrapper)).toBe(true)
        window.document.body.click()
        await nextTick()
        expect(panelOpen(wrapper)).toBe(false)
    })

    it('does not open when disabled', async () => {
        const wrapper = mount(LanguageSelect, {
            props: { options: cultureTable, selected: [], disabled: true },
            global: { plugins: [i18n] }
        })

        await wrapper.find('[data-test="language-select-trigger"]').trigger('click')

        expect(panelOpen(wrapper)).toBe(false)
        expect(
            wrapper.find('[data-test="language-select-trigger"]').attributes('aria-disabled')
        ).toBe('true')
    })
})
