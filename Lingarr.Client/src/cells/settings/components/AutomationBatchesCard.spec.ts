// @vitest-environment happy-dom
// AutomationBatchesCard (screen-06 B): the daily catch-up row renders cron +
// limit + delays from the store, the switch writes automation_enabled, and
// the edit modal echoes + saves the four batch parameters decoupled from the
// switch. The「每周全库巡检」batch has no server counterpart and is not shown.
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn())

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

beforeEach(() => {
    setSettingMock.mockClear().mockResolvedValue(undefined)
})

async function mountCard(settings: Record<string, string> = {}) {
    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    const defaults: Record<string, string> = {
        [SETTINGS.AUTOMATION_ENABLED]: 'true',
        [SETTINGS.TRANSLATION_SCHEDULE]: '0 4 * * *',
        [SETTINGS.MAX_TRANSLATIONS_PER_RUN]: '50',
        [SETTINGS.MOVIE_AGE_THRESHOLD]: '48',
        [SETTINGS.SHOW_AGE_THRESHOLD]: '24'
    }
    for (const [key, value] of Object.entries({ ...defaults, ...settings })) {
        settingStore.storeSetting(key as keyof ISettings, value)
    }

    const wrapper = mount(AutomationBatchesCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, settingStore, toastStore: useToastStore(pinia) }
}

describe('AutomationBatchesCard · 行渲染', () => {
    it('renders exactly one batch row (daily catch-up) with the derived hint', async () => {
        const { wrapper } = await mountCard()

        expect(wrapper.findAll('[data-test^="batch-row-"]')).toHaveLength(1)
        const row = wrapper.find('[data-test="batch-row-daily"]')
        expect(row.text()).toContain(en.settings.automation.dailyRow)
        expect(row.text()).toContain('0 4 * * *')
        expect(row.text()).toContain('50')
        expect(row.text()).toContain('48')
        expect(row.text()).toContain('24')
    })

    it('never renders the weekly full-library scan (no server job)', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.text()).not.toContain('weekly')
    })
})

describe('AutomationBatchesCard · 开关', () => {
    it('writes automation_enabled without touching the batch parameters', async () => {
        const { wrapper, settingStore } = await mountCard()

        await wrapper.find('[data-test="batch-row-daily"] [role="switch"]').trigger('click')
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.AUTOMATION_ENABLED)).toBe('false')
        expect(settingStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE)).toBe('0 4 * * *')
        const keys = setSettingMock.mock.calls.map((call) => call[0])
        expect(keys).toEqual(['automation_enabled'])
    })
})

describe('AutomationBatchesCard · 编辑(参数与开关解耦)', () => {
    it('offers exactly one close entry and a constant Save label (UI 批一 · 1/2)', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()

        const modal = document.querySelector('[data-test="batch-modal"]')!
        const closeButtons = [...modal.querySelectorAll('button')].filter(
            (button) => button.textContent?.trim() === en.common.close
        )
        expect(closeButtons).toHaveLength(1)
        expect(closeButtons[0].getAttribute('data-test')).toBe('batch-modal-close')

        const save = modal.querySelector('[data-test="batch-save"]') as HTMLButtonElement
        expect(save.textContent?.trim()).toBe(en.common.save)
        expect(save.textContent?.trim()).not.toBe(en.common.saved)
    })

    it('echoes the stored parameters when the modal opens', async () => {
        const { wrapper } = await mountCard()

        await wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()

        const modal = document.querySelector('[data-test="batch-modal"]')
        expect(modal).not.toBeNull()
        const values = [...(modal?.querySelectorAll('input') ?? [])].map(
            (input) => (input as HTMLInputElement).value
        )
        expect(values).toEqual(['0 4 * * *', '50', '48', '24'])
    })

    it('saves the four parameters in one pass and toasts', async () => {
        const { wrapper, toastStore } = await mountCard()

        await wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()
        ;(document.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['translation_schedule']).toBe('0 4 * * *')
        expect(written['max_translations_per_run']).toBe('50')
        expect(written['movie_age_threshold']).toBe('48')
        expect(written['show_age_threshold']).toBe('24')
        // automation_enabled untouched by the edit modal
        expect(setSettingMock.mock.calls.map((call) => call[0])).not.toContain('automation_enabled')
        expect(toastStore.toasts[0].message).toContain(en.common.saved)
    })

    it('blocks saving when the cron is missing (cron 必填)', async () => {
        const { wrapper } = await mountCard({ [SETTINGS.TRANSLATION_SCHEDULE]: '' })

        await wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()
        ;(document.querySelector('[data-test="batch-save"]') as HTMLButtonElement).click()
        await flushPromises()

        // save stays disabled with an empty cron and nothing is written
        const save = document.querySelector('[data-test="batch-save"]') as HTMLButtonElement
        expect(save.disabled).toBe(true)
        expect(setSettingMock).not.toHaveBeenCalled()
    })
})
