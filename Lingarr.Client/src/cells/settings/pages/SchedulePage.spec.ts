// @vitest-environment happy-dom
// Schedule screen (prototype screen-12 full form): the six queue-depth
// cards aggregate the depth endpoint plus the pipeline success rate, rows
// toggle enable/disable through the store, and the「＋ New job」CTA opens
// a preset menu with a custom-cron form.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { DOMWrapper, flushPromises, mount, VueWrapper } from '@vue/test-utils'
import SchedulePage from '@/cells/settings/pages/SchedulePage.vue'
import en from '@/i18n/locales/en.json'
import { useToastStore } from '@/cells/app/stores/toast'
import { useUiStore } from '@/cells/app/stores/ui'
import { IQueueDepth, IRecurringJob } from '@/types'

const scheduleApi = vi.hoisted(() => ({
    recurringJobs: vi.fn(),
    depth: vi.fn(),
    enableJob: vi.fn(),
    disableJob: vi.fn(),
    createJob: vi.fn(),
    startJob: vi.fn(),
    indexShows: vi.fn(),
    indexMovies: vi.fn(),
    remove: vi.fn()
}))
const statisticsApi = vi.hoisted(() => ({
    getPipelineStatistics: vi.fn()
}))

vi.mock('@/cells/settings/services/scheduleService', () => ({
    scheduleService: () => scheduleApi
}))
vi.mock('@/cells/app/api/statisticsService', () => ({
    statisticsService: () => statisticsApi
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

const depths: IQueueDepth[] = [
    { queue: 'movies', enqueued: 0, processing: 0 },
    { queue: 'shows', enqueued: 2, processing: 0 },
    { queue: 'system', enqueued: 1, processing: 0 },
    { queue: 'translation', enqueued: 12, processing: 2 },
    { queue: 'webhook', enqueued: 0, processing: 0 },
    { queue: 'default', enqueued: 0, processing: 0 }
]

let wrapper: VueWrapper | null = null

/// The custom-cron form teleports to body — queries go through the document.
const q = (selector: string): DOMWrapper<Element> | null => {
    const element = document.querySelector(selector)
    return element ? new DOMWrapper(element) : null
}

const mountPage = async (): Promise<VueWrapper> => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } },
            { path: '/settings/system', name: 'settings-system', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
    wrapper = mount(SchedulePage, {
        attachTo: document.body,
        global: { plugins: [createPinia(), router, i18n] }
    })
    await flushPromises()
    return wrapper
}

beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(scheduleApi.recurringJobs).mockReset()
    vi.mocked(scheduleApi.depth).mockReset()
    vi.mocked(scheduleApi.enableJob).mockReset()
    vi.mocked(scheduleApi.disableJob).mockReset()
    vi.mocked(scheduleApi.createJob).mockReset()
    vi.mocked(statisticsApi.getPipelineStatistics).mockReset()

    scheduleApi.recurringJobs.mockResolvedValue([makeJob()])
    scheduleApi.depth.mockResolvedValue(depths)
    statisticsApi.getPipelineStatistics.mockResolvedValue({ successRate30Days: 98.2 })
    scheduleApi.enableJob.mockResolvedValue(undefined)
    scheduleApi.disableJob.mockResolvedValue(undefined)
    scheduleApi.createJob.mockResolvedValue({ id: 'x', cron: 'y', jobMethod: 'z', enabled: true })
})

afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
})

describe('SchedulePage (screen-12)', () => {
    it('renders the six depth cards from the depth endpoint and the success rate', async () => {
        const page = await mountPage()

        const cards = page.findAll('[data-test="schedule-qdepth"] > div')
        expect(cards).toHaveLength(6)
        expect(cards[0].text()).toContain('12') // translation queued (accent card)
        expect(cards[1].text()).toContain('2') // executing (processing sum)
        expect(cards[2].text()).toContain('1') // system queued
        expect(cards[3].text()).toContain('2') // movies + shows
        expect(cards[4].text()).toContain('0') // webhook + default
        expect(cards[5].text()).toContain('98.2%') // pipeline success rate
    })

    it('renders an em-dash success rate when statistics are unavailable', async () => {
        statisticsApi.getPipelineStatistics.mockResolvedValue({ successRate30Days: null })
        const page = await mountPage()

        const cards = page.findAll('[data-test="schedule-qdepth"] > div')
        expect(cards[5].text()).toContain('—')
    })

    it('shows dimmed rows with an Enable action for disabled jobs', async () => {
        scheduleApi.recurringJobs.mockResolvedValue([
            makeJob({ id: 'CleanupJob', cron: '0 2 * * 0', enabled: false })
        ])
        const page = await mountPage()

        const enableButton = page.find('[data-test="schedule-enable"]')
        expect(enableButton.exists()).toBe(true)
        expect(enableButton.text()).toBe(en.settings.schedule.enable)
        expect(page.find('[data-test="schedule-disable"]').exists()).toBe(false)
    })

    it('enabling a job calls the endpoint, refreshes and toasts the job id', async () => {
        scheduleApi.recurringJobs.mockResolvedValue([
            makeJob({ id: 'CleanupJob', enabled: false })
        ])
        const page = await mountPage()

        await page.find('[data-test="schedule-enable"]').trigger('click')
        await flushPromises()

        expect(scheduleApi.enableJob).toHaveBeenCalledWith('CleanupJob')
        expect(scheduleApi.recurringJobs).toHaveBeenCalledTimes(2) // initial fetch + post-toggle refresh
        const toast = useToastStore()
        expect(toast.toasts[0].message).toBe('Enabled: CleanupJob')
        expect(toast.toasts[0].type).toBe('success')
    })

    it('disabling an enabled job calls the disable endpoint and toasts', async () => {
        const page = await mountPage()

        await page.find('[data-test="schedule-disable"]').trigger('click')
        await flushPromises()

        expect(scheduleApi.disableJob).toHaveBeenCalledWith('StatisticsJob')
        expect(useToastStore().toasts[0].message).toBe('Disabled: StatisticsJob')
    })

    it('registers the new-job CTA that opens the preset menu', async () => {
        const page = await mountPage()

        const uiStore = useUiStore()
        expect(uiStore.cta?.label).toBe(en.settings.schedule.newJob)

        expect(page.find('[data-test="schedule-new-job-menu"]').exists()).toBe(false)
        uiStore.cta?.handler()
        await flushPromises()
        expect(page.find('[data-test="schedule-new-job-menu"]').exists()).toBe(true)

        // one preset per registered job type + the custom-cron entry
        const items = page.findAll('[data-test="schedule-new-job-menu"] [role="menuitem"]')
        expect(items).toHaveLength(6)
        expect(page.find('[data-test="schedule-custom-cron"]').exists()).toBe(true)
    })

    it('creating from a preset posts the definition and refreshes the list', async () => {
        const page = await mountPage()

        const uiStore = useUiStore()
        uiStore.cta?.handler()
        await flushPromises()

        const preset = page
            .findAll('[data-test="schedule-new-job-menu"] [role="menuitem"]')
            .find((item) => item.text().includes('Statistics snapshot'))
        await preset!.trigger('click')
        await flushPromises()

        expect(scheduleApi.createJob).toHaveBeenCalledWith({
            jobId: 'statistics-snapshot',
            jobType: 'StatisticsJob',
            cron: '0 3 * * *'
        })
        expect(useToastStore().toasts[0].message).toBe('Created statistics-snapshot · 0 3 * * *')
    })

    it('the custom-cron form offers exactly one close entry — the footer button (UI 批一 · 1)', async () => {
        const page = await mountPage()

        const uiStore = useUiStore()
        uiStore.cta?.handler()
        await flushPromises()
        await page.find('[data-test="schedule-custom-cron"]').trigger('click')
        await flushPromises()

        const form = q('[data-test="schedule-new-job-form"]')
        expect(form).not.toBeNull()
        const closeButtons = [...form!.element.querySelectorAll('button')].filter(
            (button) => button.textContent?.trim() === en.common.close
        )
        expect(closeButtons).toHaveLength(1)
        expect(closeButtons[0].getAttribute('data-test')).toBe('schedule-form-close')
    })

    it('the custom-cron form validates and submits job id, type and cron', async () => {
        const page = await mountPage()

        const uiStore = useUiStore()
        uiStore.cta?.handler()
        await flushPromises()
        await page.find('[data-test="schedule-custom-cron"]').trigger('click')
        await flushPromises()

        const form = q('[data-test="schedule-new-job-form"]')
        expect(form).not.toBeNull()
        expect(q('[data-test="schedule-create-save"]')!.attributes('disabled')).toBeDefined()

        const inputs = form!.findAll('input')
        await inputs[0].setValue('nightly-scan')
        await form!.find('select').setValue('StatisticsJob')
        await inputs[1].setValue('0 5 * * *')
        await flushPromises()

        expect(q('[data-test="schedule-create-save"]')!.attributes('disabled')).toBeUndefined()

        await q('[data-test="schedule-create-save"]')!.trigger('click')
        await flushPromises()

        expect(scheduleApi.createJob).toHaveBeenCalledWith({
            jobId: 'nightly-scan',
            jobType: 'StatisticsJob',
            cron: '0 5 * * *'
        })
    })

    it('surfaces a 409 as the already-exists toast', async () => {
        scheduleApi.createJob.mockRejectedValue(
            Object.assign(new Error('conflict'), { response: { status: 409 }, isAxiosError: true })
        )
        const page = await mountPage()

        const uiStore = useUiStore()
        uiStore.cta?.handler()
        await flushPromises()
        const preset = page
            .findAll('[data-test="schedule-new-job-menu"] [role="menuitem"]')
            .find((item) => item.text().includes('Weekly cleanup'))
        await preset!.trigger('click')
        await flushPromises()

        const toast = useToastStore()
        expect(toast.toasts[0].type).toBe('error')
        expect(toast.toasts[0].message).toBe(en.settings.schedule.jobExists)
    })

    it('refetches jobs and depths through the reload button', async () => {
        const page = await mountPage()

        scheduleApi.recurringJobs.mockClear()
        scheduleApi.depth.mockClear()
        await page.find('[data-test="schedule-view-queue"]').exists() // page settled
        page.findComponent({ name: 'ReloadComponent' }).vm.$emit('toggle:update')
        await flushPromises()

        expect(scheduleApi.recurringJobs).toHaveBeenCalled()
        expect(scheduleApi.depth).toHaveBeenCalled()
    })
})
