import { AxiosStatic } from 'axios'
import { IStatisticsService } from '@/types'

const service = (http: AxiosStatic, resource = '/api/statistics'): IStatisticsService => ({
    getStatistics<T>(): Promise<T> {
        return http.get(resource).then((response) => response.data)
    },

    getDailyStatistics<T>(days: number = 30): Promise<T> {
        return http.get(`${resource}/daily/${days}`).then((response) => response.data)
    },

    getPipelineStatistics() {
        return http.get(`${resource}/pipeline`).then((response) => response.data)
    },

    resetStatistics(): Promise<void> {
        return http.post(`${resource}/reset`).then((response) => response.data)
    }
})

export const statisticsService = (axios: AxiosStatic): IStatisticsService => {
    return service(axios)
}
