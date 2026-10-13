import axios, { AxiosError } from 'axios'
import { baseUrl } from '@/utils/baseUrl'
import { ApiError } from '@/services/apiError'

axios.defaults.baseURL = baseUrl()

/**
 * Converts any thrown value into a normalised `ApiError`.
 * - Extends `Error`, so `instanceof Error` checks in components work.
 * - Keeps the raw backend payload on `.data`, so `err?.data?.message`
 *   keeps working for existing call sites.
 */
export const toApiError = (error: unknown): unknown => {
    if (error instanceof AxiosError) {
        const response = error.response
        const payload = response?.data as { message?: string } | undefined
        return new ApiError({
            message: payload?.message || response?.statusText || error.message,
            status: response?.status,
            statusText: response?.statusText,
            data: response?.data
        })
    }
    return error
}

axios.interceptors.response.use(
    (response) => response,
    (error) => {
        const prefix = baseUrl().replace(/\/$/, '')
        const loginPath = `${prefix}/auth/login`
        if (error?.response?.status === 401) {
            const currentPath = window.location.pathname
            if (!currentPath.startsWith(loginPath)) {
                window.location.href = loginPath
            }
        }
        // Always reject with a normalised error — never leave the promise
        // hanging after a redirect.
        return Promise.reject(toApiError(error))
    }
)
