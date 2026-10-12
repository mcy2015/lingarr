import { describe, expect, it } from 'vitest'
import { ApiError } from '@/services/apiError'
import { buildUrl } from '@/utils/string'
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

describe('buildUrl', () => {
    it('appends and joins query parameters', () => {
        const result = buildUrl('/api/media/movies', {
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

    it('returns the url unchanged without parameters', () => {
        expect(buildUrl('/api/media', {})).toBe('/api/media')
    })

    it('encodes reserved characters', () => {
        const result = buildUrl('/api', { q: 'a b&c=d' })

        expect(result).toBe('/api?q=a%20b%26c%3Dd')
    })

    it('appends to an existing query string', () => {
        const result = buildUrl('/api/media?pageNumber=1', { searchQuery: 'dune' })

        expect(result).toBe('/api/media?pageNumber=1&searchQuery=dune')
    })
})
