import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useToastStore } from '@/store/toast'

describe('useToastStore', () => {
    beforeEach(() => {
        setActivePinia(createPinia())
    })

    it('push adds a toast, auto-increments ids and defaults the type to info', () => {
        const store = useToastStore()

        store.push({ message: 'saved', type: 'success' })
        store.push({ message: 'deleted' })

        expect(store.toasts).toHaveLength(2)
        expect(store.toasts[0]).toMatchObject({ message: 'saved', type: 'success' })
        expect(store.toasts[1]).toMatchObject({ message: 'deleted', type: 'info' })
        expect(store.toasts[0].id).not.toBe(store.toasts[1].id)
    })

    it('auto-dismisses a toast at 5000ms but not at 4999ms', () => {
        vi.useFakeTimers()
        const store = useToastStore()

        store.push({ message: 'saved', type: 'success' })

        vi.advanceTimersByTime(4999)
        expect(store.toasts).toHaveLength(1)

        vi.advanceTimersByTime(1)
        expect(store.toasts).toHaveLength(0)
    })

    it('dismiss removes only the targeted toast', () => {
        const store = useToastStore()

        store.push({ message: 'first' })
        store.push({ message: 'second' })
        const target = store.toasts[0]

        store.dismiss(target.id)

        expect(store.toasts).toHaveLength(1)
        expect(store.toasts[0].id).not.toBe(target.id)
        expect(store.toasts[0].message).toBe('second')
    })

    afterEach(() => {
        vi.useRealTimers()
    })
})
