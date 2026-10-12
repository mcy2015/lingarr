import { createI18n } from 'vue-i18n'
import en from './locales/en.json'
import zhCN from './locales/zh-CN.json'

export const SUPPORTED_LOCALES = [
    { code: 'en', label: 'English' },
    { code: 'zh-CN', label: '简体中文' }
] as const

export type LocaleCode = (typeof SUPPORTED_LOCALES)[number]['code']

const STORAGE_KEY = 'lingarr-locale'

/**
 * Resolves the initial locale in order of preference:
 * persisted choice -> browser language -> English.
 */
const detectLocale = (): LocaleCode => {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored && SUPPORTED_LOCALES.some((l) => l.code === stored)) {
        return stored as LocaleCode
    }
    if (navigator.language.toLowerCase().startsWith('zh')) {
        return 'zh-CN'
    }
    return 'en'
}

const i18n = createI18n({
    legacy: false,
    locale: detectLocale(),
    fallbackLocale: 'en',
    messages: {
        en,
        'zh-CN': zhCN
    }
})

/**
 * Switches the active locale, persists the choice and
 * keeps <html lang> in sync for accessibility.
 */
export const setLocale = (locale: LocaleCode) => {
    i18n.global.locale.value = locale
    localStorage.setItem(STORAGE_KEY, locale)
    document.documentElement.lang = locale
}

document.documentElement.lang = i18n.global.locale.value

export default i18n
