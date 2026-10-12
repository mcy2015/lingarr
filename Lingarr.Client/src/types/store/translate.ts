import { ILanguage } from '@/types'

export interface IUseTranslateStore {
    languages: ILanguage[]
    languagesError: boolean
    languagesLoading: boolean
}
