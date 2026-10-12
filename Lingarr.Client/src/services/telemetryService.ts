import { AxiosStatic } from 'axios'
import { ITelemetryService } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/telemetry'): ITelemetryService => ({
    preview<T>(): Promise<T> {
        return http.get(`${resource}/preview`).then((response) => response.data)
    }
})

export const telemetryService = (axios: AxiosStatic): ITelemetryService => {
    return service(axios)
}
