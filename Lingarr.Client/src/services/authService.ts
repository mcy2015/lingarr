import { AxiosStatic } from 'axios'
import {
    IApiKeyResponse,
    IAuthService,
    ILoginRequest,
    IOnboardingRequest,
    ISignupRequest,
    IUser,
    IUpdateUserRequest
} from '@/types'

const service = (http: AxiosStatic, resource = '/api/auth'): IAuthService => ({
    completeOnboarding(request: IOnboardingRequest): Promise<void> {
        return http
            .post(`${resource}/onboarding`, {
                enableUserAuth: request.enableUserAuth,
                enableApiKey: request.enableApiKey
            })
            .then((response) => response.data)
    },

    signup(request: ISignupRequest): Promise<void> {
        return http.post(`${resource}/signup`, request).then((response) => response.data)
    },

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
    },

    getUsers(): Promise<IUser[]> {
        return http.get(`${resource}/users`).then((response) => response.data)
    },

    updateUser(id: number, request: IUpdateUserRequest): Promise<void> {
        return http.put(`${resource}/users/${id}`, request).then((response) => response.data)
    },

    deleteUser(id: number): Promise<void> {
        return http.delete(`${resource}/users/${id}`).then((response) => response.data)
    }
})

export const authService = (axios: AxiosStatic): IAuthService => {
    return service(axios)
}
