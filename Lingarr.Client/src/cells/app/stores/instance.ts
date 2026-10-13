import { acceptHMRUpdate, defineStore } from 'pinia'
import axios from 'axios'
import { IUseInstanceStore, ITheme, THEMES, IMovie, IShow, IVersion } from '@/types'
import { useLocalStorage } from '@/cells/kernel/composables/useLocalStorage'
import { settingService } from '@/cells/app/api/settingService'
import { authService } from '@/cells/app/api/authService'
import services from '@/services'

const localStorage = useLocalStorage()
const settingApi = settingService(axios)
const authApi = authService(axios)

export const useInstanceStore = defineStore('instance', {
    state: (): IUseInstanceStore => ({
        version: {
            newVersion: false,
            isDevelopment: false,
            currentVersion: '',
            latestVersion: ''
        },
        isOpen: false,
        theme: THEMES.LINGARR,
        poster: '',
        authenticated: false
    }),
    getters: {
        getVersion: (state: IUseInstanceStore): IVersion => state.version,
        getTheme: (state: IUseInstanceStore): ITheme => state.theme,
        getIsOpen: (state: IUseInstanceStore): boolean => state.isOpen,
        getPoster: (state: IUseInstanceStore): string => state.poster
    },
    actions: {
        setIsOpen(isOpen: boolean): void {
            this.isOpen = isOpen
        },
        async ensureAuthenticated(): Promise<boolean> {
            if (this.authenticated) {
                return true
            }

            try {
                await authApi.authenticated()
                this.authenticated = true
                return true
            } catch (error: unknown) {
                console.log(error)
                return false
            }
        },
        setPoster({ content, type }: { content: IMovie | IShow; type: string }): void {
            if (!content || !Array.isArray(content.images)) {
                this.poster = ''
                return
            }
            const posterImage = content.images.find((image) => image.type === 'poster')
            this.poster = posterImage ? `${type}${posterImage.path}` : ''
        },
        async applyVersionOnLoad(): Promise<void> {
            let version = localStorage.getItem<IVersion>('version')
            version = (await services.version.getVersion<IVersion>()) ?? version
            this.setVersion(version)
        },
        setVersion(version: IVersion): void {
            localStorage.setItem('version', version)
            this.version = version
        },
        setTheme(theme: ITheme): void {
            localStorage.setItem('theme', theme)
            this.theme = theme
            document.documentElement.className = theme
        },
        storeTheme(theme: ITheme): void {
            settingApi.setSetting('theme', theme)
            this.setTheme(theme)
        }
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useInstanceStore, import.meta.hot))
}
