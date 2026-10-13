/**
 * Normalised error thrown by every service call (via the axios response
 * interceptor in `cells/app/http.ts`).
 *
 * - `instanceof Error` works, so component-level `err instanceof Error`
 *   checks behave correctly.
 * - `.data` carries the raw backend payload, so `err?.data?.message`
 *   call sites keep working.
 */
export class ApiError extends Error {
    readonly status?: number
    readonly statusText?: string
    readonly data?: unknown

    constructor(options: {
        message: string
        status?: number
        statusText?: string
        data?: unknown
    }) {
        super(options.message)
        this.name = 'ApiError'
        this.status = options.status
        this.statusText = options.statusText
        this.data = options.data
    }
}
