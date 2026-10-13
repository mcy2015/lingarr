/// Linear time extrapolation shared by the queue rows and the request
/// detail screen (prototype screens 03 / 09): with a start time and a
/// percentage done, the remaining work is estimated proportionally.
/// App-level util because both screens live in the queue cell but the
/// same math backs any future consumer.

const DAY_MS = 86_400_000

/**
 * Remaining milliseconds estimated from elapsed time and progress, or
 * null when the estimate is not derivable (no start, no progress, or an
 * extrapolation beyond a day — treated as noise rather than a real ETA).
 */
export const remainingMs = (
    startedAt: string | null | undefined,
    progress: number,
    now: number = Date.now()
): number | null => {
    if (!startedAt || progress <= 0) {
        return null
    }
    const elapsed = now - new Date(startedAt).getTime()
    if (elapsed <= 0) {
        return null
    }
    const remaining = (elapsed / progress) * (100 - progress)
    if (!(remaining > 0) || remaining >= DAY_MS) {
        return null
    }
    return remaining
}

/** Elapsed milliseconds between a start and an end (default: now). */
export const elapsedMs = (
    startedAt: string | null | undefined,
    endedAt?: string | null | undefined,
    now: number = Date.now()
): number | null => {
    if (!startedAt) {
        return null
    }
    const start = new Date(startedAt).getTime()
    const end = endedAt ? new Date(endedAt).getTime() : now
    const elapsed = end - start
    return elapsed > 0 ? elapsed : null
}

/**
 * mm:ss clock, widening to h:mm:ss once an hour is exceeded
 * ("04:12", "1:02:13").
 */
export const formatClock = (ms: number): string => {
    const totalSeconds = Math.round(ms / 1000)
    const seconds = totalSeconds % 60
    const minutes = Math.floor(totalSeconds / 60) % 60
    const hours = Math.floor(totalSeconds / 3600)
    const mm = String(minutes).padStart(2, '0')
    const ss = String(seconds).padStart(2, '0')
    return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`
}
