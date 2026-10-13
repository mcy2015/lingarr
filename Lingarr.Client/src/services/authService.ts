import { AxiosStatic } from 'axios'
import { IApiKeyResponse, IAuthService, ILoginRequest } from '@/types'

const service = (http: AxiosStatic, resource = '/api/auth'): IAuthService => ({
    login(request: ILoginRequest): Promise<void> {
        return http.post(`${resource}/login`, request).then((response) => response.data)
    },

    authenticated(): Promise<void> {
        return http.get(`${resource}/authenticated`).then((response) => response.data)
    },

    logout(): Promise<void> {
        return http.post(`${resource}/logout`).then((response) => response.data)
    },

    generateApiKey(): Promise<IApiKeyResponse> {
        return http.post(`${resource}/apikey/generate`).then((response) => response.data)
    },

    hasAnyUsers(): Promise<boolean> {
        return http.get(`${resource}/users/any`).then((response) => response.data)
    }
})

export const authService = (axios: AxiosStatic): IAuthService => {
    return service(axios)
}
