import { describe, expect, it } from 'vitest'
import { ApiError } from '@/services/apiError'
import '@/utils'

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

describe('String prototype helpers', () => {
    it('addParams appends and joins query parameters', () => {
        const result = '/api/media/movies'.addParams({
            pageNumber: 1,
            searchQuery: 'dune',
            ascending: true
        })

        expect(result.startsWith('/api/media/movies?')).toBe(true)
        expect(result).toContain('pageNumber=1')
        expect(result).toContain('searchQuery=dune')
        expect(result).toContain('ascending=true')
        expect(result.split('?')[1].split('&')).toHaveLength(3)
    })

    it('addParams returns the url unchanged without parameters', () => {
        expect('/api/media'.addParams({})).toBe('/api/media')
    })

    it('addParams encodes reserved characters', () => {
        const result = '/api'.addParams({ q: 'a b&c=d' })

        expect(result).toBe('/api?q=a%20b%26c%3Dd')
    })
})
