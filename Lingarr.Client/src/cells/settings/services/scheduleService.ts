import { AxiosStatic } from 'axios'
import { IScheduleService } from '@/types'

const service = (http: AxiosStatic, resource = '/api/schedule'): IScheduleService => ({
    startJob<T>(jobName: string): Promise<T> {
        return http.post(`${resource}/job/start`, { jobName }).then((response) => response.data)
    },
    recurringJobs<T>(): Promise<T> {
        return http.get(`${resource}/jobs`).then((response) => response.data)
    },
    remove<T>(jobId: string): Promise<T> {
        return http.delete(`${resource}/job/remove/${jobId}`).then((response) => response.data)
    },
    indexShows<T>(): Promise<T> {
        return http.post(`${resource}/job/index/shows`).then((response) => response.data)
    },
    indexMovies<T>(): Promise<T> {
        return http.post(`${resource}/job/index/movies`).then((response) => response.data)
    }
})

export const scheduleService = (axios: AxiosStatic): IScheduleService => {
    return service(axios)
}
