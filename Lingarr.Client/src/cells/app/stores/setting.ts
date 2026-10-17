import { acceptHMRUpdate, defineStore } from 'pinia'
import {
    IEncryptedSettingStates,
    ILanguage,
    IPluginSettingField,
    ISettings,
    IUseSettingStore,
    ENCRYPTED_SETTINGS,
    PLUGIN_SETTING_TYPE,
    SETTINGS
} from '@/types'
import axios from 'axios'
import i18n from '@/i18n'
import { settingService } from '@/cells/app/api/settingService'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { useToastStore } from '@/cells/app/stores/toast'

const settingApi = settingService(axios)

export const useSettingStore = defineStore('setting', {
    state: (): IUseSettingStore => ({
        settings: {} as ISettings,
        encrypted_settings: {} as IEncryptedSettingStates
    }),
    getters: {
        getSettings: (state: IUseSettingStore): ISettings => {
            return {
                ...state.settings,
                source_languages: parseLanguages(state.settings.source_languages),
                target_languages: parseLanguages(state.settings.target_languages)
            }
        },
        getSetting: (state: IUseSettingStore) => (key: keyof ISettings) => state.settings[key],
        /// Security walk fix: encrypted settings are boolean "configured"
        /// flags server-side — the store never holds secret plaintext.
        isEncryptedSettingConfigured: (state: IUseSettingStore) => (key: keyof IEncryptedSettingStates) =>
            state.encrypted_settings[key] === true
    },
    actions: {
        async updateSetting(
            key: keyof ISettings,
            value: string | boolean | unknown[],
            isValid: boolean,
            isJson: boolean = false
        ): Promise<void> {
            if (typeof value === 'string' && (key === 'radarr_url' || key === 'sonarr_url')) {
                value = value.replace(/\/+$/, '')
            }

            this.storeSetting(key, value)
            if (isValid) {
                if (isJson) {
                    if (!allKeysHaveValues(value)) return
                    await this.saveSetting(key, JSON.stringify(value))
                } else {
                    await this.saveSetting(key, value as string)
                }
            }
        },
        async updateEncryptedSetting(
            key: keyof IEncryptedSettingStates,
            value: string,
            isValid: boolean = true
        ): Promise<boolean> {
            // Write-only mirror: locally we keep just the configured flag.
            // The flag flips optimistically and rolls back when the server
            // refuses the write, so the UI never claims "configured" for a
            // value that was not stored.
            const previous = this.encrypted_settings[key]
            this.encrypted_settings[key] = value.trim() !== ''
            if (!isValid) return false
            try {
                await settingApi.setEncryptedSetting(key, value)
                return true
            } catch {
                this.encrypted_settings[key] = previous
                // Failure feedback lives here, not in the callers: every
                // caller fires this action fire-and-forget from a computed
                // setter that cannot await it.
                useToastStore().push({
                    message: i18n.global.t('common.saveFailed'),
                    type: 'error'
                })
                return false
            }
        },
        /// Local-only flag sync for flows whose endpoint already persisted the
        /// value (auth generate/revoke store or clear the key server-side), so
        /// the UI state follows without a redundant settings write.
        setEncryptedSettingState(key: keyof IEncryptedSettingStates, configured: boolean) {
            this.encrypted_settings[key] = configured
        },
        storeSetting(key: keyof ISettings, value: string | boolean | unknown[]) {
            this.settings[key] = value as never
        },
        async saveSetting(key: keyof ISettings, value: string) {
            await settingApi.setSetting(key, value)
        },

        async setPluginSettings(fields: IPluginSettingField[]): Promise<void> {
            const plainKeys: string[] = []
            const encryptedKeys: string[] = []
            for (const field of fields) {
                if (field.type === PLUGIN_SETTING_TYPE.SECRET) {
                    encryptedKeys.push(field.key)
                } else {
                    plainKeys.push(field.key)
                }
            }

            const [plainValues, encryptedStates] = await Promise.all([
                plainKeys.length > 0
                    ? settingApi.getSettings<Partial<ISettings>>(plainKeys)
                    : Promise.resolve({} as Partial<ISettings>),
                encryptedKeys.length > 0
                    ? settingApi.getEncryptedSettingStates<Partial<IEncryptedSettingStates>>(encryptedKeys)
                    : Promise.resolve({} as Partial<IEncryptedSettingStates>)
            ])

            this.settings = { ...this.settings, ...plainValues }
            this.encrypted_settings = { ...this.encrypted_settings, ...encryptedStates }
        },

        async applySettingsOnLoad(): Promise<void> {
            const instanceStore = useInstanceStore()
            const [settings, encryptedStates] = await Promise.all([
                settingApi.getSettings<ISettings>(Object.values(SETTINGS)),
                settingApi.getEncryptedSettingStates<IEncryptedSettingStates>(
                    Object.values(ENCRYPTED_SETTINGS)
                )
            ])

            this.settings = {
                ...settings,
                source_languages: JSON.parse(settings.source_languages as string),
                target_languages: JSON.parse(settings.target_languages as string)
            }
            this.encrypted_settings = encryptedStates

            instanceStore.storeTheme(settings.theme)
        }
    }
})

function parseLanguages(value: string | ILanguage[] | undefined): ILanguage[] {
    // applySettingsOnLoad stores these already parsed; other load paths (e.g.
    // setPluginSettings) leave raw JSON strings. Accept both — JSON.parsing the
    // parsed array once crashed the whole /import page on render.
    if (Array.isArray(value)) return value
    if (typeof value === 'string' && value !== '') {
        try {
            return JSON.parse(value)
        } catch {
            return []
        }
    }
    return []
}

function allKeysHaveValues(values: string | boolean | unknown[]) {
    // If values is not an array, return false immediately
    if (!Array.isArray(values)) {
        return true
    }

    // Check if every item in the array is a valid object
    const isArrayOfObjects = values.every(
        (item): item is Record<string, unknown> =>
            item !== null && typeof item === 'object' && !Array.isArray(item)
    )

    if (!isArrayOfObjects) {
        return false
    }

    // Now TypeScript knows values is an array of objects
    return values.every((obj) => {
        return Object.values(obj).every(
            (value) => value !== null && value !== undefined && value !== ''
        )
    })
}

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useSettingStore, import.meta.hot))
}
