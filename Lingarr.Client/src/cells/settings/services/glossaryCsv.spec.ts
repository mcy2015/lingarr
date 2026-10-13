// Glossary CSV parser (screen-06 C「导入 CSV」): optional header, standard
// CSV quoting, mode mapping (locked/context/锁定/上下文, case-insensitive)
// and malformed-row skipping.
import { describe, expect, it } from 'vitest'
import { parseGlossaryCsv } from '@/cells/settings/services/glossaryCsv'

describe('parseGlossaryCsv', () => {
    it('parses plain rows without a header and defaults the mode to context', () => {
        const result = parseGlossaryCsv('Ye Wenjie,叶文洁\nWallfacer,面壁者')

        expect(result.skipped).toBe(0)
        expect(result.entries).toEqual([
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', matchMode: 'context' },
            { sourceTerm: 'Wallfacer', targetTerm: '面壁者', matchMode: 'context' }
        ])
    })

    it('skips an optional header row', () => {
        const result = parseGlossaryCsv('source,target,mode\nYe Wenjie,叶文洁,locked')

        expect(result.skipped).toBe(0)
        expect(result.entries).toEqual([
            { sourceTerm: 'Ye Wenjie', targetTerm: '叶文洁', matchMode: 'locked' }
        ])
    })

    it('accepts a header of any case and without the mode column', () => {
        const result = parseGlossaryCsv('Source,Target\nSophon,智子')

        expect(result.entries).toHaveLength(1)
        expect(result.entries[0].matchMode).toBe('context')
    })

    it('maps every accepted mode spelling case-insensitively', () => {
        const csv = [
            'a,一,locked',
            'b,二,LOCKED',
            'c,三,context',
            'd,四,Context',
            'e,五,锁定',
            'f,六,上下文'
        ].join('\n')

        const result = parseGlossaryCsv(csv)

        expect(result.skipped).toBe(0)
        expect(result.entries.map((entry) => entry.matchMode)).toEqual([
            'locked',
            'locked',
            'context',
            'context',
            'locked',
            'context'
        ])
    })

    it('keeps commas inside quoted fields and unescapes doubled quotes', () => {
        const csv = '"Wall, facer",面壁者\n"say ""hi""",说“你好”'

        const result = parseGlossaryCsv(csv)

        expect(result.skipped).toBe(0)
        expect(result.entries).toEqual([
            { sourceTerm: 'Wall, facer', targetTerm: '面壁者', matchMode: 'context' },
            { sourceTerm: 'say "hi"', targetTerm: '说“你好”', matchMode: 'context' }
        ])
    })

    it('handles CRLF line endings and ignores blank lines', () => {
        const result = parseGlossaryCsv('a,一\r\n\r\nb,二\r\n')

        expect(result.entries).toHaveLength(2)
        expect(result.skipped).toBe(0)
    })

    it('skips malformed rows and counts them', () => {
        const csv = [
            'only-source',        // no target term
            ',无源',              // no source term
            'c,三,weird-mode',    // unknown mode
            '"unterminated,quote' // broken quoting
        ].join('\n')

        const result = parseGlossaryCsv(csv)

        expect(result.entries).toEqual([])
        expect(result.skipped).toBe(4)
    })

    it('returns an empty result for an empty file', () => {
        const result = parseGlossaryCsv('')

        expect(result.entries).toEqual([])
        expect(result.skipped).toBe(0)
    })
})
