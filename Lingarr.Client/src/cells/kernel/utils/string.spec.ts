import { describe, expect, it } from 'vitest'
import { buildUrl, maskSecret } from '@/cells/kernel/utils/string'

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

describe('maskSecret', () => {
    it('keeps the first and last 4 characters around a fixed bullet run', () => {
        expect(maskSecret('abcdefghijkl')).toBe('abcd•••ijkl')
    })

    it('masks short values entirely with a fixed-width mask that hides the length', () => {
        expect(maskSecret('short')).toBe('••••••••')
        expect(maskSecret('12345678')).toBe('••••••••')
        expect(maskSecret('')).toBe('••••••••')
        expect(maskSecret('ab')).toBe('••••••••')
    })

    it('never returns the original value', () => {
        const secret = '9'.repeat(64)
        expect(maskSecret(secret)).not.toContain(secret)
        expect(maskSecret(secret)).toBe(`9999•••${'9'.repeat(4)}`)
    })

    it('supports a custom edge size', () => {
        expect(maskSecret('abcdefgh', 2)).toBe('ab•••gh')
        expect(maskSecret('abcd', 2)).toBe('••••••••')
    })
})
