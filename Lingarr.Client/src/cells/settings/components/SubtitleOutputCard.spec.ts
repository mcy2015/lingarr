// @vitest-environment happy-dom
// SubtitleOutputCard (screen-06 C): every former SubtitleSettings toggle and
// ValidationSettings control survives as a setlist row (nothing dropped), the
// naming row is read-only, toggles write the store, and the validation rows
// appear only when validation is on.
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import SubtitleOutputCard from '@/cells/settings/components/SubtitleOutputCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

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

    it('renders the naming row read-only (server-fixed naming, no edit control)', async () => {
        const { wrapper } = await mountCard()

        const naming = wrapper.find('[data-test="subtitle-row-naming"]')
        expect(naming.exists()).toBe(true)
        expect(naming.text()).toContain('{original}.{language}.srt')
        expect(naming.findAll('button')).toHaveLength(0)
        expect(naming.findAll('input')).toHaveLength(0)
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
