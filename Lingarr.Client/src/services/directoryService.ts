import { AxiosStatic } from 'axios'
import { IDirectoryService } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/directory'): IDirectoryService => ({
    get(path: string): Promise<[]> {
        return http
            .get(`${resource}/get`.addParams({ path: path }))
            .then((response) => response.data)
    }
})

export const directoryService = (axios: AxiosStatic): IDirectoryService => {
    return service(axios)
}
