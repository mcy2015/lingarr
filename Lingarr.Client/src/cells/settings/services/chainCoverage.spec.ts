// P1-3 chain coverage math: per source × target pair, count how many chain
// members can serve it — AI-style "unrestricted" services count as available,
// restricted ones are checked against their language table, unreadable tables
// count as unknown (never silently available).
import { describe, expect, it } from 'vitest'
import { computeChainCoverage, pairSupported, ServiceLanguageTable } from '@/cells/settings/services/chainCoverage'

const restricted = (codes: string[]): ServiceLanguageTable => ({
    serviceType: 'deepl',
    status: 'restricted',
    languages: codes.map((code) => ({ code, name: code }))
})

const unrestricted = (serviceType = 'openai'): ServiceLanguageTable => ({
    serviceType,
    status: 'unrestricted',
    languages: []
})

const unknown = (serviceType: string): ServiceLanguageTable => ({
    serviceType,
    status: 'unknown',
    languages: []
})

describe('pairSupported', () => {
    it('serves any culture-valid pair when the service ships no language table', () => {
        expect(pairSupported(unrestricted(), 'en', 'zh')).toBe(true)
    })

    it('requires both codes on the table for restricted services, with neutral-root matching', () => {
        const table = restricted(['en', 'en-US', 'ja', 'zh'])
        expect(pairSupported(table, 'en', 'zh')).toBe(true)
        expect(pairSupported(table, 'en-US', 'zh')).toBe(true) // en-US resolves to root en
        expect(pairSupported(table, 'en', 'ko')).toBe(false)
    })

    it('returns null for unreadable tables instead of guessing', () => {
        expect(pairSupported(unknown('deepl'), 'en', 'zh')).toBeNull()
    })
})

describe('computeChainCoverage', () => {
    const sources = ['en', 'ja']
    const targets = ['zh']

    it('reports full coverage when every chain member can serve the pair', () => {
        const rows = computeChainCoverage(
            ['openai', 'deepl', 'ollama'],
            { openai: unrestricted(), deepl: restricted(['en', 'ja', 'zh']), ollama: unrestricted('ollama') },
            sources,
            targets
        )

        expect(rows).toHaveLength(2)
        expect(rows[0]).toMatchObject({ source: 'EN', target: 'ZH', available: 3, total: 3, missing: [], unknown: 0 })
        expect(rows[1]).toMatchObject({ source: 'JA', target: 'ZH', available: 3, total: 3, missing: [] })
    })

    it('lists the members that cannot serve a pair', () => {
        const rows = computeChainCoverage(
            ['openai', 'deepl', 'ollama'],
            { openai: unrestricted(), deepl: restricted(['en', 'fr']), ollama: unrestricted('ollama') },
            sources,
            targets
        )

        const ja = rows.find((row) => row.source === 'JA')
        expect(ja).toMatchObject({ available: 2, total: 3 })
        expect(ja?.missing).toEqual(['deepl'])
    })

    it('counts unreadable tables as unknown, not available', () => {
        const rows = computeChainCoverage(
            ['openai', 'deepl'],
            { openai: unrestricted(), deepl: unknown('deepl') },
            sources,
            targets
        )

        expect(rows[0]).toMatchObject({ available: 1, total: 2, unknown: 1, missing: [] })
    })

    it('marks a pair with zero available providers', () => {
        const rows = computeChainCoverage(
            ['deepl'],
            { deepl: restricted(['en', 'fr']) },
            ['ja'],
            targets
        )

        expect(rows[0]).toMatchObject({ available: 0, total: 1, missing: ['deepl'] })
    })
})
