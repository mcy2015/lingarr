import { defineStore } from 'pinia'

/**
 * A translatable topbar entry. Pages that register a sub/CTA at mount time
 * pass the i18n key (plus interpolation params) instead of a pre-translated
 * string, so the topbar can re-translate when the locale switches. Plain
 * strings remain supported and render as-is.
 */
export interface UiI18nEntry {
    key: string
    params?: Record<string, unknown>
}

/**
 * Per-screen chrome that pages contribute to the app-shell topbar
 * (prototype: ⌘K search + a screen-specific primary CTA). Pages register
 * their CTA on mount and clear it on unmount; the library search box
 * reacts to focus ticks so ⌘K works from anywhere.
 */
export const useUiStore = defineStore('ui', {
    state: () => ({
        cta: null as { label: string | UiI18nEntry; handler: () => void } | null,
        /** Optional per-screen subtitle override (e.g. live counts). */
        subOverride: '' as string | UiI18nEntry,
        searchFocusTick: 0
    }),
    actions: {
        registerCta(label: string | UiI18nEntry, handler: () => void) {
            this.cta = { label, handler }
        },
        clearCta() {
            this.cta = null
        },
        setSubOverride(sub: string | UiI18nEntry) {
            this.subOverride = sub
        },
        focusSearch() {
            this.searchFocusTick++
        }
    }
})
