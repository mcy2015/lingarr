import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useUiStore } from '@/cells/app/stores/ui'

describe('useUiStore', () => {
    beforeEach(() => {
        setActivePinia(createPinia())
    })

    it('registerCta stores the label and handler, and invoking cta.handler runs the callback', () => {
        const store = useUiStore()
        const handler = vi.fn()

        store.registerCta('Translate', handler)

        expect(store.cta).toEqual({ label: 'Translate', handler })
        store.cta?.handler()
        expect(handler).toHaveBeenCalledOnce()
    })

    it('clearCta resets the cta to null', () => {
        const store = useUiStore()
        store.registerCta('Translate', () => {})

        store.clearCta()

        expect(store.cta).toBeNull()
    })

    it('setSubOverride overrides the subtitle', () => {
        const store = useUiStore()

        store.setSubOverride('x')

        expect(store.subOverride).toBe('x')
    })

    it('focusSearch increments searchFocusTick on every call', () => {
        const store = useUiStore()
        expect(store.searchFocusTick).toBe(0)

        store.focusSearch()
        expect(store.searchFocusTick).toBe(1)

        store.focusSearch()
        expect(store.searchFocusTick).toBe(2)
    })
})
