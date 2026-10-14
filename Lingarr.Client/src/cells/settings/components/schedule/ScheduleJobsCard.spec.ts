// @vitest-environment happy-dom
// ScheduleJobsCard (自 screen-12 下钻屏收编进自动化 tab, IA 重构批 3):任务表
// 行启停走 store、cron 列是全产品唯一编辑入口(含停用行)。任务目录固定为
// 5 个内置任务——「＋ 新建任务」已移除(创建 API 一并删除,存量 custom 行
// 仍可管理);队列深度/成功率已随下钻屏退役。
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { DOMWrapper, flushPromises, mount, VueWrapper } from '@vue/test-utils'
import ScheduleJobsCard from '@/cells/settings/components/schedule/ScheduleJobsCard.vue'
import en from '@/i18n/locales/en.json'
import { useToastStore } from '@/cells/app/stores/toast'
import { IRecurringJob } from '@/types'

const scheduleApi = vi.hoisted(() => ({
    recurringJobs: vi.fn(),
    timeZone: vi.fn(),
    depth: vi.fn(),
    enableJob: vi.fn(),
    disableJob: vi.fn(),
    updateJobCron: vi.fn(),
    startJob: vi.fn(),
    indexShows: vi.fn(),
    indexMovies: vi.fn(),
    remove: vi.fn()
}))

vi.mock('@/cells/settings/services/scheduleService', () => ({
    scheduleService: () => scheduleApi
}))

// joining the JobProgress hub would negotiate over the network — stub it
const signalrMocks = vi.hoisted(() => {
    const hub = {
        joinGroup: vi.fn((): Promise<void> => Promise.resolve()),
        send: vi.fn((): Promise<void> => Promise.resolve()),
        on: vi.fn(),
        off: vi.fn()
    }
    return { hub, connect: vi.fn((): Promise<typeof hub> => Promise.resolve(hub)) }
})
vi.mock('@/cells/app/composables/useSignalR', () => ({
    useSignalR: () => ({ state: { hubs: {} }, connect: signalrMocks.connect })
}))

// ── fixtures ───────────────────────────────────────────────────────
const makeJob = (overrides: Partial<IRecurringJob> = {}): IRecurringJob =>
    ({
        id: 'StatisticsJob',
        cron: '0 3 * * *',
        queue: 'system',
        jobMethod: 'Execute',
        nextExecution: '2026-10-14T03:00:00Z',
        lastJobId: '12',
        lastJobState: 'Succeeded',
        lastExecution: '2026-10-13T03:00:00Z',
        createdAt: '2026-01-01T00:00:00Z',
        timeZoneId: null,
        currentState: 'Succeeded',
        isCurrentlyRunning: false,
        currentJobId: '12',
        enabled: true,
        ...overrides
    }) as IRecurringJob

let wrapper: VueWrapper | null = null

/// Teleported modals live on body — queries go through the document.
const q = (selector: string): DOMWrapper<Element> | null => {
    const element = document.querySelector(selector)
    return element ? new DOMWrapper(element) : null
}

const mountCard = async (): Promise<VueWrapper> => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
    wrapper = mount(ScheduleJobsCard, {
        attachTo: document.body,
        global: { plugins: [createPinia(), router, i18n] }
    })
    await flushPromises()
    return wrapper
}

beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(scheduleApi.recurringJobs).mockReset()
    vi.mocked(scheduleApi.timeZone).mockReset()
    vi.mocked(scheduleApi.enableJob).mockReset()
    vi.mocked(scheduleApi.disableJob).mockReset()
    vi.mocked(scheduleApi.updateJobCron).mockReset()

    scheduleApi.recurringJobs.mockResolvedValue([makeJob()])
    scheduleApi.timeZone.mockResolvedValue({ ianaId: 'Europe/Amsterdam', warning: null })
    scheduleApi.enableJob.mockResolvedValue(undefined)
    scheduleApi.disableJob.mockResolvedValue(undefined)
    scheduleApi.updateJobCron.mockResolvedValue(undefined)
})

afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
})

describe('ScheduleJobsCard (自动化 tab · 任务表)', () => {
    it('renders as the jobs card with the job rows and no depth strip', async () => {
        const page = await mountCard()

        expect(page.find('[data-test="jobs-card"]').exists()).toBe(true)
        expect(page.find('[data-test="schedule-row-StatisticsJob"]').exists()).toBe(true)
        // monitoring numbers retired from settings — the dashboard owns them
        expect(page.find('[data-test="schedule-qdepth"]').exists()).toBe(false)
    })

    it('shows dimmed rows with an Enable action for disabled jobs', async () => {
        scheduleApi.recurringJobs.mockResolvedValue([
            makeJob({ id: 'CleanupJob', cron: '0 2 * * 0', enabled: false })
        ])
        const page = await mountCard()

        const enableButton = page.find('[data-test="schedule-enable"]')
        expect(enableButton.exists()).toBe(true)
        expect(enableButton.text()).toBe(en.settings.schedule.enable)
        expect(page.find('[data-test="schedule-disable"]').exists()).toBe(false)
    })

    it('enabling a job calls the endpoint, refreshes and toasts the job id', async () => {
        scheduleApi.recurringJobs.mockResolvedValue([
            makeJob({ id: 'CleanupJob', enabled: false })
        ])
        const page = await mountCard()

        await page.find('[data-test="schedule-enable"]').trigger('click')
        await flushPromises()

        expect(scheduleApi.enableJob).toHaveBeenCalledWith('CleanupJob')
        expect(scheduleApi.recurringJobs).toHaveBeenCalledTimes(2) // initial fetch + post-toggle refresh
        const toast = useToastStore()
        expect(toast.toasts[0].message).toBe('Enabled: CleanupJob')
        expect(toast.toasts[0].type).toBe('success')
    })

    it('disabling an enabled job calls the disable endpoint and toasts', async () => {
        const page = await mountCard()

        await page.find('[data-test="schedule-disable"]').trigger('click')
        await flushPromises()

        expect(scheduleApi.disableJob).toHaveBeenCalledWith('StatisticsJob')
        expect(useToastStore().toasts[0].message).toBe('Disabled: StatisticsJob')
    })

    it('exposes no new-job entry — the catalogue is the five fixed jobs', async () => {
        const page = await mountCard()

        expect(page.find('[data-test="schedule-new-job"]').exists()).toBe(false)
        expect(page.find('[data-test="schedule-new-job-menu"]').exists()).toBe(false)
        expect(page.find('[data-test="schedule-custom-cron"]').exists()).toBe(false)
        expect(page.find('[data-test="schedule-new-job-form"]').exists()).toBe(false)
    })

    // ── cron 编辑:任务表 = 唯一编辑入口 ────────────────────────────

    it('refetches jobs through the reload button', async () => {
        const page = await mountCard()

        scheduleApi.recurringJobs.mockClear()
        await page.find('[data-test="schedule-view-queue"]').exists() // page settled
        page.findComponent({ name: 'ReloadComponent' }).vm.$emit('toggle:update')
        await flushPromises()

        expect(scheduleApi.recurringJobs).toHaveBeenCalled()
    })

    it('edits a cron through the row modal: PUT, refresh, toast, close', async () => {
        const page = await mountCard()

        await page.find('[data-test="schedule-cron-edit-StatisticsJob"]').trigger('click')
        await flushPromises()

        const modal = q('[data-test="schedule-cron-modal"]')
        expect(modal).not.toBeNull()
        const input = modal!.element.querySelector('input') as HTMLInputElement
        expect(input.value).toBe('0 3 * * *')

        input.value = '30 5 * * *'
        input.dispatchEvent(new Event('input'))
        await flushPromises()
        await q('[data-test="schedule-cron-save"]')!.trigger('click')
        await flushPromises()

        expect(scheduleApi.updateJobCron).toHaveBeenCalledWith('StatisticsJob', '30 5 * * *')
        expect(scheduleApi.recurringJobs).toHaveBeenCalledTimes(2)
        expect(useToastStore().toasts[0].type).toBe('success')
        expect(q('[data-test="schedule-cron-modal"]')).toBeNull()
    })

    it('blocks saving an invalid cron in the edit modal', async () => {
        const page = await mountCard()

        await page.find('[data-test="schedule-cron-edit-StatisticsJob"]').trigger('click')
        await flushPromises()

        const input = q('[data-test="schedule-cron-modal"]')!.element.querySelector(
            'input'
        ) as HTMLInputElement
        input.value = '77 * * * *'
        input.dispatchEvent(new Event('input'))
        await flushPromises()

        expect(
            q('[data-test="schedule-cron-save"]')!.attributes('disabled')
        ).toBeDefined()
        expect(scheduleApi.updateJobCron).not.toHaveBeenCalled()
    })

    it('offers cron editing on disabled rows too', async () => {
        scheduleApi.recurringJobs.mockResolvedValue([
            makeJob({ id: 'CleanupJob', enabled: false })
        ])
        const page = await mountCard()

        expect(page.find('[data-test="schedule-cron-edit-CleanupJob"]').exists()).toBe(true)
    })

    // ── 时区标注:cron 与「下次执行」同源时区,防按浏览器时区误读 ────

    it('labels the next-execution column with the effective time zone', async () => {
        const page = await mountCard()

        const label = page.find('[data-test="schedule-timezone-id"]')
        expect(label.exists()).toBe(true)
        expect(label.text()).toBe('Europe/Amsterdam')
    })

    it('hides the time zone label while the endpoint has not answered', async () => {
        scheduleApi.timeZone.mockReturnValue(new Promise(() => {})) // never settles
        const page = await mountCard()

        expect(page.find('[data-test="schedule-timezone-id"]').exists()).toBe(false)
    })

    it('shows the interpretation zone inside the cron edit modal', async () => {
        const page = await mountCard()

        await page.find('[data-test="schedule-cron-edit-StatisticsJob"]').trigger('click')
        await flushPromises()

        const hint = q('[data-test="schedule-cron-timezone"]')
        expect(hint).not.toBeNull()
        expect(hint!.text()).toContain('Europe/Amsterdam')
    })

    it('warns when the server fell back to UTC, and stays silent otherwise', async () => {
        scheduleApi.timeZone.mockResolvedValue({
            ianaId: 'UTC',
            warning: 'TZ is not set, falling back to UTC.'
        })
        const warned = await mountCard()

        const banner = warned.find('[data-test="schedule-timezone-warning"]')
        expect(banner.exists()).toBe(true)
        expect(banner.text()).toContain(en.settings.schedule.timeZoneWarning)
        expect(banner.attributes('title')).toBe('TZ is not set, falling back to UTC.')
        warned.unmount()
        wrapper = null

        // restore the beforeEach default (no warning) for the silent mount
        scheduleApi.timeZone.mockResolvedValue({ ianaId: 'Europe/Amsterdam', warning: null })
        const quiet = await mountCard()
        expect(quiet.find('[data-test="schedule-timezone-warning"]').exists()).toBe(false)
    })
})
