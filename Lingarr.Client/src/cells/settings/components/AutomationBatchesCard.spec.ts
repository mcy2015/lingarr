// @vitest-environment happy-dom
// AutomationBatchesCard (screen-06 B): the daily catch-up row renders cron +
// limit + delays from the store, the switch toggles the recurring job through
// the schedule enable/disable endpoint (persist + immediate registration — a
// bare setting write never registered anything), and the edit modal echoes +
// saves the four batch parameters decoupled from the switch. The「每周全库巡检」
// batch has no server counterpart and is not shown.
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
const disableJobMock = vi.hoisted(() => vi.fn())
const enableJobMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

vi.mock('@/cells/settings/services/scheduleService', () => ({
    scheduleService: () => ({
        disableJob: disableJobMock,
        enableJob: enableJobMock,
        recurringJobs: vi.fn().mockResolvedValue([])
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
    disableJobMock.mockClear().mockResolvedValue(undefined)
    enableJobMock.mockClear().mockResolvedValue(undefined)
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
    it('renders the daily catch-up row with the derived hint', async () => {
        const { wrapper } = await mountCard()

        const row = wrapper.find('[data-test="batch-row-daily"]')
        expect(row.exists()).toBe(true)
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

describe('AutomationBatchesCard · 媒体源同步 cron 行组(自 MediaSourcesCard 迁入)', () => {
    it('renders the movie/show indexer rows with the stored cron values', async () => {
        const { wrapper } = await mountCard({
            [SETTINGS.MOVIE_SCHEDULE]: '0 4 * * *',
            [SETTINGS.SHOW_SCHEDULE]: '0 5 * * *'
        })

        const movie = wrapper.find('[data-test="batch-row-movie-index"]')
        expect(movie.text()).toContain(en.settings.sources.movieIndexer)
        expect(movie.text()).toContain(en.settings.automation.indexingDescription)
        expect(movie.text()).toContain('0 4 * * *')

        const show = wrapper.find('[data-test="batch-row-show-index"]')
        expect(show.text()).toContain(en.settings.sources.showIndexer)
        expect(show.text()).toContain('0 5 * * *')
    })

    it('echoes the stored cron in the index edit modal and saves it back', async () => {
        const { wrapper } = await mountCard({
            [SETTINGS.MOVIE_SCHEDULE]: '0 4 * * *'
        })

        await wrapper.find('[data-test="batch-edit-movie-index"]').trigger('click')
        await flushPromises()

        const modal = document.querySelector('[data-test="index-modal"]')
        expect(modal).not.toBeNull()
        const input = modal?.querySelector('input') as HTMLInputElement
        expect(input.value).toBe('0 4 * * *')

        input.value = '0 6 * * *'
        input.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(document.querySelector('[data-test="index-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['movie_schedule']).toBe('0 6 * * *')
        expect(document.querySelector('[data-test="index-modal"]')).toBeNull()
    })

    it('blocks saving an invalid cron in the index modal', async () => {
        const { wrapper } = await mountCard({
            [SETTINGS.SHOW_SCHEDULE]: '0 5 * * *'
        })

        await wrapper.find('[data-test="batch-edit-show-index"]').trigger('click')
        await flushPromises()

        const input = document.querySelector(
            '[data-test="index-modal"] input'
        ) as HTMLInputElement
        input.value = 'not-a-cron'
        input.dispatchEvent(new Event('input'))
        await flushPromises()

        const save = document.querySelector('[data-test="index-save"]') as HTMLButtonElement
        expect(save.disabled).toBe(true)
        expect(setSettingMock).not.toHaveBeenCalled()
    })
})

describe('AutomationBatchesCard · 开关', () => {
    it('toggles the recurring job through the schedule endpoint', async () => {
        const { wrapper, settingStore, toastStore } = await mountCard()

        await wrapper.find('[data-test="batch-row-daily"] [role="switch"]').trigger('click')
        await flushPromises()

        // the job endpoint persists + registers; no bare setting write
        expect(disableJobMock).toHaveBeenCalledWith('AutomatedTranslationJob')
        expect(setSettingMock.mock.calls.map((call) => call[0])).not.toContain('automation_enabled')
        // the local cache follows so the switch reflects the new state
        expect(settingStore.getSetting(SETTINGS.AUTOMATION_ENABLED)).toBe('false')
        expect(settingStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE)).toBe('0 4 * * *')
        expect(toastStore.toasts[0].message).toContain(en.common.saved)
    })

    it('leaves the switch state untouched when the endpoint fails', async () => {
        disableJobMock.mockRejectedValueOnce(new Error('boom'))
        const { wrapper, settingStore, toastStore } = await mountCard()

        await wrapper.find('[data-test="batch-row-daily"] [role="switch"]').trigger('click')
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.AUTOMATION_ENABLED)).toBe('true')
        expect(toastStore.toasts[0].message).toContain(en.settings.automation.toggleFailed)
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
