// QA P3-4: degradation edge copy renders with the typographic arrow in every
// surface that shows it, regardless of the ASCII form the server persisted.
import { describe, expect, it } from 'vitest'
import { formatDegradationEdge } from '@/cells/app/utils/degradationEdge'

describe('formatDegradationEdge', () => {
    it('rewrites the persisted ASCII arrow to the typographic one', () => {
        expect(formatDegradationEdge('gemini -> openai')).toBe('gemini → openai')
    })

    it('rewrites every edge in a multi-edge message', () => {
        expect(formatDegradationEdge('gemini -> openai -> deepl')).toBe('gemini → openai → deepl')
    })

    it('leaves messages without an edge untouched', () => {
        expect(formatDegradationEdge('429 · Too many requests')).toBe('429 · Too many requests')
        expect(formatDegradationEdge('')).toBe('')
    })
})
