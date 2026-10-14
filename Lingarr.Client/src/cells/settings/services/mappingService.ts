import { AxiosStatic } from 'axios'
import { IPathMapping, IMappingService, IMappingStatus, ITestMappingResult } from '@/types'

const service = (http: AxiosStatic, resource = '/api/mapping'): IMappingService => ({
    getMappings(): Promise<IPathMapping[]> {
        return http.get(`${resource}/get`).then((response) => response.data)
    },

    setMappings(mappings: IPathMapping[]): Promise<void> {
        return http.post(`${resource}/set`, mappings).then((response) => response.data)
    },

    status(): Promise<IMappingStatus> {
        return http.get(`${resource}/status`).then((response) => response.data)
    },

    testMapping(path: string, mediaType: string): Promise<ITestMappingResult> {
        return http
            .post(`${resource}/test`, { path, mediaType })
            .then((response) => response.data)
    }
})

export const mappingService = (axios: AxiosStatic): IMappingService => {
    return service(axios)
}
