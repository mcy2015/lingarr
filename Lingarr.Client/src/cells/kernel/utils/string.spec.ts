import { describe, expect, it } from 'vitest'
import { buildUrl } from '@/cells/kernel/utils/string'

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
