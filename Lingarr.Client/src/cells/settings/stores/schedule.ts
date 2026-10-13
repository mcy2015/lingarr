import { acceptHMRUpdate, defineStore } from 'pinia'
import axios from 'axios'
import {
    IUseScheduleStore,
    IRecurringJob,
    IQueueDepth,
    ICreateRecurringJobRequest,
    IRecurringJobStatus
} from '@/types'
import { scheduleService } from '@/cells/settings/services/scheduleService'

const scheduleApi = scheduleService(axios)

export const useScheduleStore = defineStore('schedule', {
    state: (): IUseScheduleStore => ({
        recurringJobs: [],
        queueDepths: []
    }),
    getters: {
        getRecurringJobs: (state: IUseScheduleStore): IRecurringJob[] => state.recurringJobs,
        getQueueDepths: (state: IUseScheduleStore): IQueueDepth[] => state.queueDepths
    },
    actions: {
        async fetchRecurringJobs(): Promise<void> {
            this.recurringJobs = await scheduleApi.recurringJobs()
        },
        async fetchQueueDepths(): Promise<void> {
            this.queueDepths = await scheduleApi.depth()
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
        async createJob(request: ICreateRecurringJobRequest): Promise<IRecurringJobStatus> {
            const status = await scheduleApi.createJob<IRecurringJobStatus>(request)
            await this.fetchRecurringJobs()
            return status
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
