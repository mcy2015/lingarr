import { acceptHMRUpdate, defineStore } from 'pinia'
import { ILanguage, ISubtitle, IUseTranslateStore, MediaType } from '@/types'
import axios from 'axios'
import { translateService } from '@/cells/app/api/translateService'
import useTranslationRequestStore from '@/cells/app/stores/translationRequest'

const translateApi = translateService(axios)

export const useTranslateStore = defineStore('translate', {
    state: (): IUseTranslateStore => ({
        languages: [],
        languagesError: false,
        languagesLoading: false
    }),
    getters: {
        getLanguages: (state: IUseTranslateStore): ILanguage[] => state.languages,
        hasLanguagesError: (state: IUseTranslateStore): boolean => state.languagesError,
        isLanguagesLoading: (state: IUseTranslateStore): boolean => state.languagesLoading
    },
    actions: {
        async translateSubtitle(
            mediaId: number,
            subtitle: ISubtitle,
            source: string,
            target: ILanguage,
            mediaType: MediaType
        ): Promise<number> {
            const result = await translateApi.translateSubtitle<{ jobId: number }>(
                mediaId,
                subtitle,
                source,
                target,
                mediaType
            )
            await useTranslationRequestStore().fetchActiveTranslations()
            return result.jobId
        },
        async bulkTranslate(
            mediaIds: number[],
            targetLanguage: string,
            mediaType: MediaType
        ): Promise<void> {
            await translateApi.bulkTranslate<number[]>(
                mediaIds,
                targetLanguage,
                mediaType
            )
            await useTranslationRequestStore().fetchActiveTranslations()
        },
        async setLanguages(): Promise<void> {
            try {
                this.languagesLoading = true
                this.languagesError = false
                this.languages = await translateApi.getLanguages<ILanguage[]>()
            } catch (error) {
                console.error('Failed to load languages:', error)
                this.languagesError = true
                this.languages = []
            } finally {
                this.languagesLoading = false
            }
        }
    }
})

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useTranslateStore, import.meta.hot))
}
