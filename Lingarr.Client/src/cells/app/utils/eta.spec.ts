import { describe, expect, it } from 'vitest'
import { elapsedMs, formatClock, remainingMs } from '@/cells/app/utils/eta'

const NOW = new Date('2026-10-13T12:00:00Z').getTime()
const STARTED = '2026-10-13T11:00:00Z' // one hour before NOW

describe('remainingMs (linear extrapolation)', () => {
    it('extrapolates the remaining share from elapsed time and progress', () => {
        // 60 minutes elapsed for 50% -> 60 minutes remaining
        expect(remainingMs(STARTED, 50, NOW)).toBe(3_600_000)
    })

    it('returns null without a start time or progress', () => {
        expect(remainingMs(null, 50, NOW)).toBeNull()
        expect(remainingMs(undefined, 50, NOW)).toBeNull()
        expect(remainingMs(STARTED, 0, NOW)).toBeNull()
    })

    it('returns null for a start in the future or a non-positive extrapolation', () => {
        expect(remainingMs('2026-10-13T13:00:00Z', 50, NOW)).toBeNull()
        expect(remainingMs(STARTED, 100, NOW)).toBeNull()
    })

    it('caps the estimate at one day and treats that as no ETA', () => {
        // 1% after an hour -> ~4.1 days remaining: noise, not an ETA
        expect(remainingMs(STARTED, 1, NOW)).toBeNull()
    })
})

describe('elapsedMs', () => {
    it('measures start to now by default and start to an end when given', () => {
        expect(elapsedMs(STARTED, undefined, NOW)).toBe(3_600_000)
        expect(elapsedMs(STARTED, '2026-10-13T11:30:00Z', NOW)).toBe(1_800_000)
    })

    it('returns null without a start or with a non-positive span', () => {
        expect(elapsedMs(null)).toBeNull()
        expect(elapsedMs(STARTED, '2026-10-13T10:00:00Z', NOW)).toBeNull()
    })
})

describe('formatClock', () => {
    it('renders mm:ss below an hour', () => {
        expect(formatClock(0)).toBe('00:00')
        expect(formatClock(4 * 60_000 + 12_000)).toBe('04:12')
        expect(formatClock(59 * 60_000 + 59_000)).toBe('59:59')
    })

    it('widens to h:mm:ss once an hour is exceeded', () => {
        expect(formatClock(3_600_000)).toBe('1:00:00')
        expect(formatClock(3_733_000)).toBe('1:02:13')
    })
})
