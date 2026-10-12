import { AxiosStatic } from 'axios'
import {
    IPluginManifest,
    IPluginOptionsResponse,
    IPluginStatus,
    IPluginSummary,
    IPluginService
} from '@/types'

const service = (http: AxiosStatic, resource = '/api/plugin'): IPluginService => ({
    list(): Promise<IPluginSummary[]> {
        return http.get(resource).then((response) => response.data)
    },
    getManifest(provider: string): Promise<IPluginManifest> {
        return http.get(`${resource}/${provider}/manifest`).then((response) => response.data)
    },
    getStatus(provider: string): Promise<IPluginStatus> {
        return http.get(`${resource}/${provider}/status`).then((response) => response.data)
    },
    getOptions(endpoint: string): Promise<IPluginOptionsResponse> {
        return http.get(endpoint).then((response) => response.data)
    }
})

export const pluginService = (axios: AxiosStatic): IPluginService => {
    return service(axios)
}
