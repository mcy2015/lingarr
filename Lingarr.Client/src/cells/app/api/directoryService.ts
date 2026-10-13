import { AxiosStatic } from 'axios'
import { IDirectoryService } from '@/types'
import { buildUrl } from '@/cells/kernel/utils/string'

const service = (http: AxiosStatic, resource = '/api/directory'): IDirectoryService => ({
    get(path: string): Promise<[]> {
        return http
            .get(buildUrl(`${resource}/get`, { path }))
            .then((response) => response.data)
    }
})

export const directoryService = (axios: AxiosStatic): IDirectoryService => {
    return service(axios)
}
