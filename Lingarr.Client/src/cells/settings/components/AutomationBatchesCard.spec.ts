// @vitest-environment happy-dom
// AutomationBatchesCard (设置 · 自动化 tab):AutomatedTranslationJob 的唯一前端
// 驻留位——开关读任务表真值(schedule store,任务表不再渲染该行)、行内
// 下次执行(带解释时区)、手动触发、编辑模态 cron(本卡即编辑入口)+ 三策略
// 字段,与开关解耦。「每周全库巡检」服务端不存在,不显示。
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { IRecurringJob, ISettings, SETTINGS } from '@/types'
import en from '@/i18n/locales/en.json'

const setSettingMock = vi.hoisted(() => vi.fn())
const disableJobMock = vi.hoisted(() => vi.fn())
const enableJobMock = vi.hoisted(() => vi.fn())
const updateJobCronMock = vi.hoisted(() => vi.fn())
const startJobMock = vi.hoisted(() => vi.fn())
const recurringJobsMock = vi.hoisted(() => vi.fn())
const timeZoneMock = vi.hoisted(() => vi.fn())

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
        updateJobCron: updateJobCronMock,
        startJob: startJobMock,
        recurringJobs: recurringJobsMock,
        timeZone: timeZoneMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

// ── fixtures ───────────────────────────────────────────────────────
const makeAutomationJob = (overrides: Partial<IRecurringJob> = {}): IRecurringJob =>
    ({
        id: 'AutomatedTranslationJob',
        cron: '0 4 * * *',
        queue: 'translation',
        jobMethod: 'Execute',
        nextExecution: '2026-10-15T04:00:00Z',
        lastJobId: null,
        lastJobState: null,
        lastExecution: null,
        createdAt: '2026-01-01T00:00:00Z',
        timeZoneId: null,
        currentState: 'Planned',
        isCurrentlyRunning: false,
        currentJobId: null,
        enabled: true,
        ...overrides
    }) as IRecurringJob

/// The toggle's status label renders in ToggleButton's slot (a sibling of
/// the switch button), so the row text is where it is readable.
const switchLabel = (wrapper: VueWrapper): string =>
    wrapper.find('[data-test="batch-row-daily"]').text()

type Mounted = {
    wrapper: VueWrapper
    pinia: ReturnType<typeof createPinia>
    settingStore: ReturnType<typeof useSettingStore>
    toastStore: ReturnType<typeof useToastStore>
}

async function mountCard(
    settings: Record<string, string> = {},
    jobs: IRecurringJob[] = [makeAutomationJob()]
): Promise<Mounted> {
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

    recurringJobsMock.mockReset().mockResolvedValue(jobs)
    timeZoneMock.mockReset().mockResolvedValue({ ianaId: 'Europe/Amsterdam', warning: null })

    const wrapper = mount(AutomationBatchesCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return { wrapper, pinia, settingStore, toastStore: useToastStore(pinia) }
}

beforeEach(() => {
    setSettingMock.mockClear().mockResolvedValue(undefined)
    disableJobMock.mockClear().mockResolvedValue(undefined)
    enableJobMock.mockClear().mockResolvedValue(undefined)
    updateJobCronMock.mockClear().mockResolvedValue(undefined)
    startJobMock.mockClear().mockResolvedValue(undefined)
})

describe('AutomationBatchesCard · 行渲染', () => {
    it('renders the daily catch-up row with the strategy-only hint', async () => {
        const { wrapper } = await mountCard()

        const row = wrapper.find('[data-test="batch-row-daily"]')
        expect(row.exists()).toBe(true)
        expect(row.text()).toContain(en.settings.automation.dailyRow)
        expect(row.text()).toContain('50')
        expect(row.text()).toContain('48')
        expect(row.text()).toContain('24')
        // cron moved out of the hint — it lives in the edit modal now
        expect(row.text()).not.toContain('0 4 * * *')
    })

    it('never renders the weekly full-library scan (no server job)', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.text()).not.toContain('weekly')
    })

    it('shows the next execution labelled with the interpretation zone', async () => {
        const { wrapper } = await mountCard()

        const next = wrapper.find('[data-test="batch-next-execution"]')
        expect(next.exists()).toBe(true)
        expect(next.text()).toContain('Europe/Amsterdam')
    })

    it('hides the next execution until the job list hydrates', async () => {
        // never-settling fetch: release the store's dedupe slot on teardown
        let release: (jobs: IRecurringJob[]) => void = () => {}
        recurringJobsMock.mockReset().mockReturnValue(
            new Promise<IRecurringJob[]>((resolve) => {
                release = resolve
            })
        )
        timeZoneMock.mockReset().mockResolvedValue({ ianaId: 'Europe/Amsterdam', warning: null })
        onTestFinished(() => {
            release([])
        })

        const pinia = createPinia()
        const settingStore = useSettingStore(pinia)
        settingStore.storeSetting(SETTINGS.AUTOMATION_ENABLED, 'true')
        const wrapper = mount(AutomationBatchesCard, { global: { plugins: [pinia, i18n] } })
        onTestFinished(() => wrapper.unmount())
        await flushPromises()

        // switch bridges from the settings copy — no wrong-state flash
        expect(switchLabel(wrapper)).toContain(en.settings.status.enabled)
        expect(wrapper.find('[data-test="batch-next-execution"]').exists()).toBe(false)
    })
})

describe('AutomationBatchesCard · 开关(任务真值单源)', () => {
    it('derives the switch state from the recurring job, not the settings copy', async () => {
        // settings say enabled, live job says disabled — the job wins
        const { wrapper } = await mountCard(
            { [SETTINGS.AUTOMATION_ENABLED]: 'true' },
            [makeAutomationJob({ enabled: false, nextExecution: null })]
        )

        expect(switchLabel(wrapper)).toContain(en.settings.status.disabled)
    })

    it('toggles through the job endpoint and follows the refetched state', async () => {
        const mounted = await mountCard()
        // refetch after the toggle returns the disabled registration
        recurringJobsMock.mockResolvedValue([makeAutomationJob({ enabled: false })])

        await mounted.wrapper
            .find('[data-test="batch-row-daily"] [role="switch"]')
            .trigger('click')
        await flushPromises()

        // the job endpoint persists + registers; no bare setting write
        expect(disableJobMock).toHaveBeenCalledWith('AutomatedTranslationJob')
        expect(setSettingMock.mock.calls.map((call) => call[0])).not.toContain('automation_enabled')
        // the switch now reads the refetched job — no reload needed
        expect(switchLabel(mounted.wrapper)).toContain(en.settings.status.disabled)
        expect(mounted.settingStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE)).toBe('0 4 * * *')
        expect(mounted.toastStore.toasts[0].message).toContain(en.common.saved)
    })

    it('leaves the switch state untouched when the endpoint fails', async () => {
        disableJobMock.mockRejectedValueOnce(new Error('boom'))
        const { wrapper, toastStore } = await mountCard()

        await wrapper.find('[data-test="batch-row-daily"] [role="switch"]').trigger('click')
        await flushPromises()

        expect(switchLabel(wrapper)).toContain(en.settings.status.enabled)
        expect(toastStore.toasts[0].message).toContain(en.settings.automation.toggleFailed)
    })

    it('offers the manual trigger only while the job is enabled', async () => {
        const { wrapper } = await mountCard()
        expect(wrapper.findComponent({ name: 'TriggerJob' }).exists()).toBe(true)

        await wrapper.findComponent({ name: 'TriggerJob' }).vm.$emit('toggle:trigger')
        expect(startJobMock).toHaveBeenCalledWith('AutomatedTranslationJob')

        const disabled = await mountCard({}, [makeAutomationJob({ enabled: false })])
        expect(disabled.wrapper.findComponent({ name: 'TriggerJob' }).exists()).toBe(false)
    })
})

describe('AutomationBatchesCard · 编辑(cron 归位本卡,参数与开关解耦)', () => {
    const openModal = async (mounted: Mounted): Promise<HTMLElement> => {
        await mounted.wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()
        return document.querySelector('[data-test="batch-modal"]')!
    }

    it('offers exactly one close entry and a constant Save label (UI 批一 · 1/2)', async () => {
        const modal = await openModal(await mountCard())

        const closeButtons = [...modal.querySelectorAll('button')].filter(
            (button) => button.textContent?.trim() === en.common.close
        )
        expect(closeButtons).toHaveLength(1)
        expect(closeButtons[0].getAttribute('data-test')).toBe('batch-modal-close')

        const save = modal.querySelector('[data-test="batch-save"]') as HTMLButtonElement
        expect(save.textContent?.trim()).toBe(en.common.save)
        expect(save.textContent?.trim()).not.toBe(en.common.saved)
    })

    it('edits the cron here: prefilled input, zone hint, no read-only row', async () => {
        const modal = await openModal(await mountCard())

        const inputs = [...(modal.querySelectorAll('input') ?? [])] as HTMLInputElement[]
        expect(inputs).toHaveLength(4) // cron + three strategy fields
        expect(inputs[0].value).toBe('0 4 * * *')

        // the interpretation zone is stated where the cron is written
        expect(modal.querySelector('[data-test="batch-cron-timezone"]')!.textContent).toContain(
            'Europe/Amsterdam'
        )
        // the read-only cron row is gone — this modal is the editor now
        expect(modal.querySelector('[data-test="batch-modal-cron"]')).toBeNull()
    })

    it('saves the three strategy parameters in one pass, skipping an unchanged cron', async () => {
        const mounted = await mountCard()
        const modal = await openModal(mounted)

        ;(modal.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['max_translations_per_run']).toBe('50')
        expect(written['movie_age_threshold']).toBe('48')
        expect(written['show_age_threshold']).toBe('24')
        // cron untouched — no re-registration for an identical expression
        expect(updateJobCronMock).not.toHaveBeenCalled()
        expect(mounted.toastStore.toasts[0].message).toContain(en.common.saved)
    })

    it('routes a changed cron through the cron endpoint alongside the strategy save', async () => {
        const mounted = await mountCard()
        const modal = await openModal(mounted)

        const cronInput = modal.querySelector('input') as HTMLInputElement
        cronInput.value = '30 5 * * *'
        cronInput.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(modal.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        expect(updateJobCronMock).toHaveBeenCalledWith('AutomatedTranslationJob', '30 5 * * *')
        expect(setSettingMock.mock.calls.map((call) => call[0])).toContain(
            'max_translations_per_run'
        )
        expect(mounted.toastStore.toasts[0].type).toBe('success')
        expect(document.querySelector('[data-test="batch-modal"]')).toBeNull()
    })

    it('blocks saving an invalid cron', async () => {
        const modal = await openModal(await mountCard())

        const cronInput = modal.querySelector('input') as HTMLInputElement
        cronInput.value = '77 * * * *'
        cronInput.dispatchEvent(new Event('input'))
        await flushPromises()

        expect(
            (modal.querySelector('[data-test="batch-save"]') as HTMLButtonElement).disabled
        ).toBe(true)
        expect(updateJobCronMock).not.toHaveBeenCalled()
    })

    it('toasts the mapped error and keeps the modal open when the cron write fails', async () => {
        updateJobCronMock.mockRejectedValueOnce({
            isAxiosError: true,
            response: { status: 400 }
        })
        const mounted = await mountCard()
        const modal = await openModal(mounted)

        const cronInput = modal.querySelector('input') as HTMLInputElement
        cronInput.value = '30 5 * * *'
        cronInput.dispatchEvent(new Event('input'))
        await flushPromises()
        ;(modal.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        expect(mounted.toastStore.toasts[0].message).toContain(en.settings.schedule.cronInvalid)
        expect(document.querySelector('[data-test="batch-modal"]')).not.toBeNull()
    })
})

describe('AutomationBatchesCard · 数值失焦钳制(R17)', () => {
    /// 模态输入序:cron → 每次运行上限 → 新片延迟(电影)→ 新片延迟(剧集)
    const openModalInputs = async (mounted: Mounted): Promise<HTMLInputElement[]> => {
        await mounted.wrapper.find('[data-test="batch-edit"]').trigger('click')
        await flushPromises()
        const modal = document.querySelector('[data-test="batch-modal"]')!
        return [...modal.querySelectorAll('input')] as HTMLInputElement[]
    }

    it('clamps the per-run limit up to 1 on blur and saves the clamped value', async () => {
        const mounted = await mountCard()
        const inputs = await openModalInputs(mounted)

        inputs[1].value = '0'
        inputs[1].dispatchEvent(new Event('input'))
        inputs[1].dispatchEvent(new Event('blur'))
        await flushPromises()

        // element and draft both carry the boundary — 0 never reaches the API
        expect(inputs[1].value).toBe('1')
        ;(document.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['max_translations_per_run']).toBe('1')
        expect(mounted.toastStore.toasts.some((toast) => toast.type === 'info')).toBe(true)
        expect(mounted.toastStore.toasts.find((toast) => toast.type === 'info')?.message).toBe(
            en.toast.validation.clamped.replace('{range}', '1–1000')
        )
    })

    it('clamps a negative new-release delay back to 0 (min-only range label)', async () => {
        const mounted = await mountCard()
        const inputs = await openModalInputs(mounted)

        inputs[2].value = '-5'
        inputs[2].dispatchEvent(new Event('input'))
        inputs[2].dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(inputs[2].value).toBe('0')
        ;(document.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['movie_age_threshold']).toBe('0')
        expect(mounted.toastStore.toasts.find((toast) => toast.type === 'info')?.message).toBe(
            en.toast.validation.clamped.replace('{range}', '≥ 0')
        )
    })

    it('leaves an in-range limit untouched on blur — no clamp toast, no boundary write', async () => {
        const mounted = await mountCard()
        const inputs = await openModalInputs(mounted)

        // '25' is within 1–1000: the blur must not rewrite it. The draft still
        // holds the prefilled '50' until the 1s debounce settles — the point
        // here is that neither the input nor a toast reports a clamp.
        inputs[1].value = '25'
        inputs[1].dispatchEvent(new Event('input'))
        inputs[1].dispatchEvent(new Event('blur'))
        await flushPromises()

        expect(inputs[1].value).toBe('25')
        expect(mounted.toastStore.toasts.some((toast) => toast.type === 'info')).toBe(false)

        ;(document.querySelector('[data-test="batch-save"]') as HTMLElement).click()
        await flushPromises()

        const written = Object.fromEntries(setSettingMock.mock.calls)
        expect(written['max_translations_per_run']).not.toBe('1')
        expect(written['max_translations_per_run']).not.toBe('1000')
    })
})
