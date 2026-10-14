// @vitest-environment happy-dom
// SubtitleOutputCard (screen-06 C): every former SubtitleSettings toggle and
// ValidationSettings control survives as a setlist row (nothing dropped), the
// naming row previews the real subtitle_naming_template setting and edits it
// through a modal (save/cancel/empty-validation), toggles write the store,
// and the validation rows appear only when validation is on.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import SubtitleOutputCard from '@/cells/settings/components/SubtitleOutputCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

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

const ALL_TOGGLE_ROWS = [
    'ignoreCaptions',
    'fixOverlapping',
    'stripFormatting',
    'preserveLineBreaks',
    'bilingualOutput',
    'addTranslatorInfo',
    'removeLanguageTag',
    'useTagging',
    'validationEnabled'
]

async function mountCard(settings: Record<string, string> = {}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    const defaults: Record<string, string> = {
        [SETTINGS.IGNORE_CAPTIONS]: 'false',
        [SETTINGS.FIX_OVERLAPPING_SUBTITLES]: 'true',
        [SETTINGS.STRIP_SUBTITLE_FORMATTING]: 'false',
        [SETTINGS.PRESERVE_LINE_BREAKS]: 'true',
        [SETTINGS.BILINGUAL_SUBTITLE_OUTPUT]: 'false',
        [SETTINGS.ADD_TRANSLATOR_INFO]: 'false',
        [SETTINGS.REMOVE_LANGUAGE_TAG]: 'false',
        [SETTINGS.USE_SUBTITLE_TAGGING]: 'false',
        [SETTINGS.SUBTITLE_VALIDATION_ENABLED]: 'false'
    }
    for (const [key, value] of Object.entries({ ...defaults, ...settings })) {
        settingStore.storeSetting(key as keyof ISettings, value)
    }

    const wrapper = mount(SubtitleOutputCard, {
        global: { plugins: [pinia, i18n] }
    })
    await flushPromises()
    return { wrapper, settingStore, pinia }
}

describe('SubtitleOutputCard · 行渲染', () => {
    it('keeps every subtitle toggle as a row — 8 subtitle switches + validation', async () => {
        const { wrapper } = await mountCard()

        for (const key of ALL_TOGGLE_ROWS) {
            expect(wrapper.find(`[data-test="subtitle-row-${key}"]`).exists(), `missing row ${key}`).toBe(
                true
            )
        }
    })

    it('renders the naming row with the default template preview and an edit control', async () => {
        const { wrapper } = await mountCard()

        const naming = wrapper.find('[data-test="subtitle-row-naming"]')
        expect(naming.exists()).toBe(true)
        expect(naming.text()).toContain('{原名}.{语言}.srt')
        expect(naming.find('[data-test="naming-edit"]').exists()).toBe(true)
    })

    it('previews a stored custom template', async () => {
        const { wrapper } = await mountCard({
            [SETTINGS.SUBTITLE_NAMING_TEMPLATE]: '{原名}-sub-{语言}'
        })

        expect(wrapper.find('[data-test="subtitle-row-naming"]').text()).toContain('{原名}-sub-{语言}.srt')
    })

    it('shows the subtitle tag value row only while tagging is on', async () => {
        const off = await mountCard({ [SETTINGS.USE_SUBTITLE_TAGGING]: 'false' })
        expect(off.wrapper.find('[data-test="subtitle-row-tagValue"]').exists()).toBe(false)

        const on = await mountCard({ [SETTINGS.USE_SUBTITLE_TAGGING]: 'true' })
        expect(on.wrapper.find('[data-test="subtitle-row-tagValue"]').exists()).toBe(true)
    })
})

describe('SubtitleOutputCard · 开关写回', () => {
    it('writes the setting when a row switch is clicked', async () => {
        const { wrapper, settingStore } = await mountCard({ [SETTINGS.BILINGUAL_SUBTITLE_OUTPUT]: 'false' })

        await wrapper
            .find('[data-test="subtitle-row-bilingualOutput"] [role="switch"]')
            .trigger('click')
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT)).toBe('true')
    })
})

describe('SubtitleOutputCard · 校验行(全量保留)', () => {
    it('reveals the five validation rows with presets only when validation is on', async () => {
        const off = await mountCard({ [SETTINGS.SUBTITLE_VALIDATION_ENABLED]: 'false' })
        expect(off.wrapper.find('[data-test="subtitle-row-minDurationMs"]').exists()).toBe(false)

        const on = await mountCard({
            [SETTINGS.SUBTITLE_VALIDATION_ENABLED]: 'true',
            [SETTINGS.SUBTITLE_VALIDATION_MINDURATIONMS]: '100'
        })
        const duration = on.wrapper.find('[data-test="subtitle-row-minDurationMs"]')
        expect(duration.exists()).toBe(true)
        expect(duration.text()).toContain(en.settings.validation.minDuration)
        // the 0.2s/0.5s/1s/1.5s presets survive
        expect(duration.findAll('button').map((button) => button.text())).toEqual([
            '0.2s',
            '0.5s',
            '1s',
            '1.5s'
        ])

        for (const key of [
            'maxDurationSecs',
            'minSubtitleLength',
            'maxSubtitleLength',
            'maxFileSizeBytes'
        ]) {
            expect(on.wrapper.find(`[data-test="subtitle-row-${key}"]`).exists()).toBe(true)
        }
    })

    it('echoes the stored numeric value in the min-duration input', async () => {
        const { wrapper } = await mountCard({
            [SETTINGS.SUBTITLE_VALIDATION_ENABLED]: 'true',
            [SETTINGS.SUBTITLE_VALIDATION_MINDURATIONMS]: '250'
        })

        const input = wrapper.find('[data-test="subtitle-row-minDurationMs"] input')
        expect((input.element as HTMLInputElement).value).toBe('250')
    })
})

describe('SubtitleOutputCard · 命名模板编辑模态', () => {
    it('offers exactly one close entry — the footer button, no header duplicate (UI 批一 · 1)', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="naming-edit"]').trigger('click')
        const modal = document.querySelector('[data-test="naming-modal"]')!

        const closeButtons = [...modal.querySelectorAll('button')].filter(
            (button) => button.textContent?.trim() === en.common.close
        )
        expect(closeButtons).toHaveLength(1)
        expect(closeButtons[0].getAttribute('data-test')).toBe('naming-cancel')

        // leave the teleported dialog closed so later tests query a clean body
        ;(modal.querySelector('[data-test="naming-cancel"]') as HTMLElement).click()
        await flushPromises()
    })

    it('keeps the save label constant before and after saving — success feedback goes to the toast (UI 批一 · 2)', async () => {
        const { wrapper, pinia } = await mountCard()

        await wrapper.find('[data-test="naming-edit"]').trigger('click')
        const save = document.querySelector('[data-test="naming-save"]') as HTMLButtonElement
        expect(save.textContent?.trim()).toBe(en.common.save)
        expect(save.textContent?.trim()).not.toBe(en.common.saved)

        save.click()
        await flushPromises()

        // success feedback is the toast, not a mutated button label
        const toastStore = useToastStore(pinia)
        expect(toastStore.toasts[0].message).toBe(en.common.saved)
        expect(toastStore.toasts[0].type).toBe('success')
        expect(document.querySelector('[data-test="naming-modal"]')).toBeNull()

        // reopened after a successful save, the label still reads Save
        await wrapper.find('[data-test="naming-edit"]').trigger('click')
        const reopened = document.querySelector('[data-test="naming-save"]') as HTMLButtonElement
        expect(reopened.textContent?.trim()).toBe(en.common.save)

        // leave the teleported dialog closed so later tests query a clean body
        ;(document.querySelector('[data-test="naming-cancel"]') as HTMLElement).click()
        await flushPromises()
    })

    it('opens the modal prefilled with the current template and saves a new one to the store', async () => {
        const { wrapper, settingStore } = await mountCard()

        await wrapper.find('[data-test="naming-edit"]').trigger('click')
        const modal = document.querySelector('[data-test="naming-modal"]')
        expect(modal).not.toBeNull()
        const input = modal!.querySelector('[data-test="naming-input"]') as HTMLInputElement
        expect(input.value).toBe('{原名}.{语言}')
        // the variable legend explains both placeholders
        expect(modal!.textContent).toContain('{原名}')
        expect(modal!.textContent).toContain('{语言}')

        input.value = '{原名}.{语言}.custom'
        input.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(modal!.querySelector('[data-test="naming-save"]') as HTMLButtonElement).click()
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.SUBTITLE_NAMING_TEMPLATE)).toBe('{原名}.{语言}.custom')
        expect(document.querySelector('[data-test="naming-modal"]')).toBeNull()
        // the row preview follows the saved template
        expect(wrapper.find('[data-test="subtitle-row-naming"]').text()).toContain(
            '{原名}.{语言}.custom.srt'
        )
    })

    it('blocks saving an empty template and cancels without writing', async () => {
        const { wrapper, settingStore } = await mountCard({
            [SETTINGS.SUBTITLE_NAMING_TEMPLATE]: '{原名}.{语言}'
        })

        await wrapper.find('[data-test="naming-edit"]').trigger('click')
        const modal = document.querySelector('[data-test="naming-modal"]')!
        const input = modal.querySelector('[data-test="naming-input"]') as HTMLInputElement

        input.value = '   '
        input.dispatchEvent(new Event('input'))
        await flushPromises()

        // empty template: error shown and save disabled
        expect(modal.querySelector('[data-test="naming-error"]')).not.toBeNull()
        expect((modal.querySelector('[data-test="naming-save"]') as HTMLButtonElement).disabled).toBe(true)

        // cancel leaves the setting untouched
        ;(modal.querySelector('[data-test="naming-cancel"]') as HTMLButtonElement).click()
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.SUBTITLE_NAMING_TEMPLATE)).toBe('{原名}.{语言}')
        expect(document.querySelector('[data-test="naming-modal"]')).toBeNull()
    })
})
