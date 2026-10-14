import { AxiosStatic } from 'axios'
import {
    IApiKeyResponse,
    IAuthenticatedResponse,
    IAuthService,
    IChangePasswordRequest,
    ILoginRequest,
    ILoginResponse
} from '@/types'

const service = (http: AxiosStatic, resource = '/api/auth'): IAuthService => ({
    login(request: ILoginRequest): Promise<ILoginResponse> {
        return http.post(`${resource}/login`, request).then((response) => response.data)
    },

    authenticated(): Promise<IAuthenticatedResponse> {
        return http.get(`${resource}/authenticated`).then((response) => response.data)
    },

    logout(): Promise<void> {
        return http.post(`${resource}/logout`).then((response) => response.data)
    },

    changePassword(request: IChangePasswordRequest): Promise<ILoginResponse> {
        return http.post(`${resource}/password`, request).then((response) => response.data)
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
