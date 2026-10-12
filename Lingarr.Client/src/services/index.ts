import axios, { AxiosError, AxiosStatic } from 'axios'
import { Services } from '@/types'
import { baseUrl } from '@/utils/baseUrl'
import { ApiError } from './apiError'
import { translationRequestService } from '@/services/translationRequestService'
import { authService } from './authService'
import { subtitleService } from './subtitleService'
import { importService } from './importService'
import { translateService } from './translateService'
import { settingService } from './settingService'
import { mediaService } from './mediaService'
import { versionService } from './versionService'
import { scheduleService } from '@/services/scheduleService'
import { mappingService } from '@/services/mappingService'
import { directoryService } from '@/services/directoryService'
import { statisticsService } from '@/services/statisticsService'
import { telemetryService } from '@/services/telemetryService'
import { logsService } from '@/services/logsService'
import { requestTemplateService } from '@/services/requestTemplateService'
import { pluginService } from '@/services/pluginService'

axios.defaults.baseURL = baseUrl()

/**
 * Converts any thrown value into a normalised `ApiError`.
 * - Extends `Error`, so `instanceof Error` checks in components work.
 * - Keeps the raw backend payload on `.data`, so `err?.data?.message`
 *   keeps working for existing call sites.
 */
const toApiError = (error: unknown): unknown => {
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
        const onboardingPath = `${prefix}/auth/onboarding`
        if (error?.response?.status === 403 && error.response?.data?.onboardingRequired) {
            window.location.href = onboardingPath
        } else if (error?.response?.status === 401) {
            const currentPath = window.location.pathname
            if (!currentPath.startsWith(loginPath) && !currentPath.startsWith(onboardingPath)) {
                window.location.href = loginPath
            }
        }
        // Always reject with a normalised error — never leave the promise
        // hanging after a redirect.
        return Promise.reject(toApiError(error))
    }
)

const services = (axios: AxiosStatic): Services => ({
    auth: authService(axios),
    setting: settingService(axios),
    subtitle: subtitleService(axios),
    import: importService(axios),
    translate: translateService(axios),
    translationRequest: translationRequestService(axios),
    version: versionService(axios),
    media: mediaService(axios),
    schedule: scheduleService(axios),
    mapping: mappingService(axios),
    directory: directoryService(axios),
    statistics: statisticsService(axios),
    logs: logsService(),
    telemetry: telemetryService(axios),
    requestTemplate: requestTemplateService(axios),
    plugin: pluginService(axios)
})

export default services(axios)
