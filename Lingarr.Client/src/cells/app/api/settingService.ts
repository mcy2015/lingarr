import { AxiosStatic } from 'axios'
import { ISettingService, ISettings } from '@/types'

const service = (http: AxiosStatic, resource = '/api/setting'): ISettingService => ({
    getSetting<T>(key: string): Promise<T> {
        return http.get(`${resource}/${key}`).then((response) => response.data)
    },
    getSettings<T>(keys: string[]): Promise<T> {
        return http.post(`${resource}/multiple/get`, keys).then((response) => response.data)
    },
    setSetting(key: string, value: string): Promise<void> {
        return http
            .post(resource, {
                Key: key,
                Value: value
            })
            .then((response) => response.data)
    },
    setSettings(settings: ISettings): Promise<void> {
        return http.post(`${resource}/multiple/set`, settings).then((response) => response.data)
    },
    /// Security walk fix: same endpoint as before, but the server now answers
    /// {key: configured} booleans instead of decrypted plaintext.
    getEncryptedSettingStates<T>(keys: string[]): Promise<T> {
        return http
            .post(`${resource}/multiple/encrypted/get`, keys)
            .then((response) => response.data)
    },
    /// Explicit per-key reveal (cookie sessions only, audited server-side):
    /// the one path besides writing that secret plaintext reaches the client.
    revealEncryptedSetting(key: string): Promise<string> {
        return http
            .get(`${resource}/encrypted/${encodeURIComponent(key)}`)
            .then((response) => (response.data?.value ?? '') as string)
    },
    setEncryptedSetting(key: string, value: string): Promise<void> {
        return http
            .post(`${resource}/encrypted`, { Key: key, Value: value })
            .then((response) => response.data)
    }
})

export const settingService = (axios: AxiosStatic): ISettingService => {
    return service(axios)
}
