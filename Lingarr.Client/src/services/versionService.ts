import { AxiosStatic } from 'axios'
import { IVersionService } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/version'): IVersionService => ({
    getVersion<T>(): Promise<T> {
        return http.get(resource).then((response) => response.data)
    }
})

export const versionService = (axios: AxiosStatic): IVersionService => {
    return service(axios)
}
