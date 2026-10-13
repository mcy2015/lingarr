import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useDebounce } from '@/cells/kernel/composables/useDebounce'

describe('useDebounce', () => {
    beforeEach(() => {
        vi.useFakeTimers()
    })

    afterEach(() => {
        vi.useRealTimers()
    })

    it('collapses calls within the delay into the last one, with its arguments', () => {
        const fn = vi.fn()
        const debounced = useDebounce(fn, 300)

        debounced('a')
        debounced('b')
        vi.advanceTimersByTime(300)

        expect(fn).toHaveBeenCalledTimes(1)
        expect(fn).toHaveBeenCalledWith('b')
    })

    it('fires exactly once when the delay elapses', () => {
        const fn = vi.fn()
        const debounced = useDebounce(fn, 300)

        debounced('a')
        expect(fn).not.toHaveBeenCalled()

        vi.advanceTimersByTime(299)
        expect(fn).not.toHaveBeenCalled()

        vi.advanceTimersByTime(1)
        expect(fn).toHaveBeenCalledTimes(1)
        expect(fn).toHaveBeenCalledWith('a')
    })

    it('fires once per call when calls are separated by more than the delay', () => {
        const fn = vi.fn()
        const debounced = useDebounce(fn, 300)

        debounced('a')
        vi.advanceTimersByTime(300)

        debounced('b')
        vi.advanceTimersByTime(300)

        expect(fn).toHaveBeenCalledTimes(2)
        expect(fn).toHaveBeenNthCalledWith(1, 'a')
        expect(fn).toHaveBeenNthCalledWith(2, 'b')
    })
})
