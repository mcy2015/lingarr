import { defineStore } from 'pinia'

/**
 * Per-screen chrome that pages contribute to the app-shell topbar
 * (prototype: ⌘K search + a screen-specific primary CTA). Pages register
 * their CTA on mount and clear it on unmount; the library search box
 * reacts to focus ticks so ⌘K works from anywhere.
 */
export const useUiStore = defineStore('ui', {
    state: () => ({
        cta: null as { label: string; handler: () => void } | null,
        /** Optional per-screen subtitle override (e.g. live counts). */
        subOverride: '' as string,
        searchFocusTick: 0
    }),
    actions: {
        registerCta(label: string, handler: () => void) {
            this.cta = { label, handler }
        },
        clearCta() {
            this.cta = null
        },
        setSubOverride(sub: string) {
            this.subOverride = sub
        },
        focusSearch() {
            this.searchFocusTick++
        }
    }
})
