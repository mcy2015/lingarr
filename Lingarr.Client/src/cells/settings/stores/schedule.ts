import { acceptHMRUpdate, defineStore } from 'pinia'
import axios from 'axios'
import { IUseScheduleStore, IRecurringJob } from '@/types'
import { scheduleService } from '@/cells/settings/services/scheduleService'

const scheduleApi = scheduleService(axios)

export const useScheduleStore = defineStore('schedule', {
    state: (): IUseScheduleStore => ({
        recurringJobs: []
    }),
    getters: {
        getRecurringJobs: (state: IUseScheduleStore): IRecurringJob[] => state.recurringJobs
    },
    actions: {
        async fetchRecurringJobs(): Promise<void> {
            this.recurringJobs = await scheduleApi.recurringJobs()
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
