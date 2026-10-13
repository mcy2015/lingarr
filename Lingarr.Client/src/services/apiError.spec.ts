import { describe, expect, it } from 'vitest'
import { ApiError } from '@/services/apiError'

describe('ApiError', () => {
    it('is an instanceof Error so component checks work', () => {
        const error = new ApiError({ message: 'boom' })

        expect(error instanceof Error).toBe(true)
        expect(error.message).toBe('boom')
        expect(error.name).toBe('ApiError')
    })

    it('carries the backend payload on .data for err?.data?.message readers', () => {
        const error = new ApiError({
            message: 'Validation failed',
            status: 400,
            data: { message: 'Validation failed', code: 42 }
        })

        expect((error.data as { message?: string })?.message).toBe('Validation failed')
        expect(error.status).toBe(400)
    })
})
