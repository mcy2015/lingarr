import { acceptHMRUpdate, defineStore } from 'pinia'
import axios from 'axios'
import { IUseScheduleStore, IRecurringJob, IQueueDepth } from '@/types'
import { scheduleService } from '@/cells/settings/services/scheduleService'

const scheduleApi = scheduleService(axios)

/// Batches card and jobs table both hydrate from the job list on mount —
/// concurrent fetches share one request; the slot clears on settle so
/// later refreshes (toggles, reload) fetch again.
let jobsFetch: Promise<void> | null = null

export const useScheduleStore = defineStore('schedule', {
    state: (): IUseScheduleStore => ({
        recurringJobs: [],
        queueDepths: [],
        timeZone: null
    }),
    getters: {
        getRecurringJobs: (state: IUseScheduleStore): IRecurringJob[] => state.recurringJobs,
        getQueueDepths: (state: IUseScheduleStore): IQueueDepth[] => state.queueDepths
    },
    actions: {
        async fetchRecurringJobs(): Promise<void> {
            jobsFetch ??= scheduleApi
                .recurringJobs<IRecurringJob[]>()
                .then((jobs) => {
                    this.recurringJobs = jobs
                })
                .finally(() => {
                    jobsFetch = null
                })
            return jobsFetch
        },
        async fetchQueueDepths(): Promise<void> {
            this.queueDepths = await scheduleApi.depth()
        },
        /// The schedule screen labels cron/next-run with the zone jobs run
        /// in — fetched separately so a failure hides the label, not the table.
        async fetchTimeZone(): Promise<void> {
            if (this.timeZone) return
            this.timeZone = await scheduleApi.timeZone()
        },
        /// Enable/disable land through the store so the row state and the
        /// depth strip stay in sync after the refresh.
        async setJobEnabled(jobId: string, enabled: boolean): Promise<void> {
            if (enabled) {
                await scheduleApi.enableJob(jobId)
            } else {
                await scheduleApi.disableJob(jobId)
            }
            await this.fetchRecurringJobs()
        },
        /// Cron edits land through the store so the table reflects the
        /// re-registered schedule right away.
        async updateJobCron(jobId: string, cron: string): Promise<void> {
            await scheduleApi.updateJobCron(jobId, cron)
            await this.fetchRecurringJobs()
        },
        async startJob(jobName: string): Promise<void> {
            await scheduleApi.startJob(jobName)
        },
        async indexShows(): Promise<void> {
            await scheduleApi.indexShows()
        },
        async indexMovies(): Promise<void> {
            await scheduleApi.indexMovies()
        }
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useScheduleStore, import.meta.hot))
}
