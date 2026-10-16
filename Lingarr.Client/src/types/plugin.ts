import { LabelValue } from '@/types'

export const PLUGIN_SETTING_TYPE = {
    TEXT: 'Text',
    URL: 'Url',
    SECRET: 'Secret',
    REMOTE_DROPDOWN: 'RemoteDropdown'
} as const

export type PluginSettingType = (typeof PLUGIN_SETTING_TYPE)[keyof typeof PLUGIN_SETTING_TYPE]

export interface IPluginSettingField {
    key: string
    label: string
    type: PluginSettingType
    required: boolean
    default?: string | null
    description?: string | null
    optionsEndpoint?: string | null
    minLength?: number | null
    validationErrorMessage?: string | null
}

export interface IPluginSummary {
    provider: string
    displayName: string
    description?: string | null
    isBuiltIn: boolean
    sourceFile?: string | null
    hasRequestTemplate: boolean
}

export interface IPluginManifest extends IPluginSummary {
    settings: IPluginSettingField[]
}

/// GET /api/subtitle-search/providers row (locked contract): one subtitle
/// source's display metadata, credential readiness and its setting fields.
/// settings reuses IPluginSettingField so DynamicPluginForm/PluginField can
/// render secret fields unchanged (only secret fields enter the settings
/// card's UI; url fields stay out of this round's scope).
export interface ISubtitleProviderManifest {
    provider: string
    displayName: string
    description: string | null
    requiresAuth: boolean
    credentialsConfigured: boolean
    settings: IPluginSettingField[]
}

export interface IPluginStatus {
    provider: string
    configured: boolean
    missingFields: string[]
}

export interface IPluginOptionsResponse {
    options?: LabelValue[] | null
    message?: string | null
}
