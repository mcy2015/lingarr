import { IVersion } from '@/types/version'

export interface IUseInstanceStore {
    version: IVersion
    isOpen: boolean
    theme: ITheme
    poster: string
    authenticated: boolean
    /// P0-3: server-side must_change_password flag — true until the seeded
    /// default credentials are replaced; drives the forced change screen.
    mustChangePassword: boolean
}

export const THEMES = {
    SOLARIZED_LIGHT: 'solarized-light',
    SOLARIZED_DARK: 'solarized-dark',
    DRACULA: 'dracula',
    NORD: 'nord',
    MONOKAI: 'monokai',
    MATERIAL_DARK: 'material-dark',
    GOTHAM: 'gotham',
    GRUVBOX: 'gruvbox',
    CYBERPUNK_NEON: 'cyberpunk-neon',
    HORIZON: 'horizon',
    SONARR: 'sonarr',
    RADARR: 'radarr',
    BAZARR: 'bazarr',
    LINGARR: 'lingarr'
} as const

export type ITheme = (typeof THEMES)[keyof typeof THEMES]

export type IFilter = {
    pageNumber: number
    searchQuery: string
    sortBy: string
    isAscending: boolean
    status?: string | null
}
export type IOptions = {
    label: string
    value: string
}
