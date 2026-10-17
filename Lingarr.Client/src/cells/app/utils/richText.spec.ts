import { describe, expect, it } from 'vitest'
import { renderSubtitleRichText } from '@/cells/app/utils/richText'

// Walk-through fix D2: subtitle <i>/<b> markup must reach the DOM as styled
// segments — never as literal tag text, and never via v-html. Everything the
// parser does not fully understand stays visible as plain text.
describe('renderSubtitleRichText', () => {
    it('returns a single plain segment for unmarked text', () => {
        expect(renderSubtitleRichText('plain line')).toEqual([{ text: 'plain line' }])
    })

    it('returns an empty array for empty input and empty tag pairs', () => {
        expect(renderSubtitleRichText('')).toEqual([])
        expect(renderSubtitleRichText('<i></i>')).toEqual([])
    })

    it('splits italic spans around plain text', () => {
        expect(renderSubtitleRichText('a <i>b</i> c')).toEqual([
            { text: 'a ' },
            { text: 'b', italic: true },
            { text: ' c' }
        ])
    })

    it('splits bold spans', () => {
        expect(renderSubtitleRichText('<b>b</b>t')).toEqual([
            { text: 'b', bold: true },
            { text: 't' }
        ])
    })

    it('marks a segment both italic and bold when properly nested', () => {
        expect(renderSubtitleRichText('<i><b>x</b></i>')).toEqual([
            { text: 'x', italic: true, bold: true }
        ])
    })

    it('matches tags case-insensitively (SubRip allows <I>)', () => {
        expect(renderSubtitleRichText('<I>x</I>')).toEqual([{ text: 'x', italic: true }])
    })

    it('treats unknown tags as plain text', () => {
        expect(renderSubtitleRichText('<u>x</u> and <font color="red">y</font>')).toEqual([
            { text: '<u>x</u> and <font color="red">y</font>' }
        ])
    })

    it('keeps an unclosed opener as literal text', () => {
        expect(renderSubtitleRichText('a <i>b')).toEqual([{ text: 'a <i>b' }])
    })

    it('keeps a stray closer as literal text', () => {
        expect(renderSubtitleRichText('a</i>b')).toEqual([{ text: 'a</i>b' }])
    })

    it('keeps a duplicate opener as visible literal text inside the open span', () => {
        // the duplicate never toggles state, so the single closer balances the
        // original opener — the stray tag stays visible, styled with its run
        expect(renderSubtitleRichText('<i>a<i>b</i>')).toEqual([{ text: 'a<i>b', italic: true }])
    })

    it('recovers to plain text when a duplicate opener is never closed', () => {
        expect(renderSubtitleRichText('<i>a<i>b')).toEqual([{ text: '<i>a<i>b' }])
    })

    it('falls back to plain text for mismatched nesting', () => {
        // </i> closes out of order while <b> is open — the whole run with the
        // unclosed <i> is re-emitted as literal text
        expect(renderSubtitleRichText('<i>a<b>c</i>d</b>')).toEqual([
            { text: '<i>a<b>c</i>d</b>' }
        ])
    })

    it('parses consecutive marked spans of different styles', () => {
        expect(renderSubtitleRichText('<i>a</i> plain <b>c</b>')).toEqual([
            { text: 'a', italic: true },
            { text: ' plain ' },
            { text: 'c', bold: true }
        ])
    })

    it('parses alternating same-style spans after an inner unclosed tag', () => {
        // the unclosed <b> voids its own run only; earlier spans survive
        expect(renderSubtitleRichText('<i>a</i>b<c')).toEqual([
            { text: 'a', italic: true },
            { text: 'b<c' }
        ])
    })
})
