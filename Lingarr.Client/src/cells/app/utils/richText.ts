/// Restricted rich-text segmentation for subtitle content (walk-through fix
/// D2): source/target lines may carry SubRip inline markup (<i>…</i>,
/// <b>…</b>). Rendering them through plain text interpolation prints the
/// tags literally, and v-html is off the table for provider-produced output —
/// so the markup is parsed here into typed segments that pages render as
/// plain spans with style classes. App-level util because both the queue
/// detail lines table and the proofread workbench consume it.
///
/// Only balanced <i>/<b> pairs (case-insensitive) become flagged segments;
/// every other tag is literal text. Stray closers, duplicate openers,
/// mismatched nesting and unclosed openers all fall back to literal text —
/// an unclosed opener re-emits the remainder of the line as one plain run.

export interface RichTextSegment {
    text: string
    italic?: boolean
    bold?: boolean
}

type RichFlag = 'italic' | 'bold'

interface MarkedRun {
    text: string
    italic: boolean
    bold: boolean
    /** source offset of the run's first character, for unclosed-tag recovery */
    start: number
}

interface OpenTag {
    flag: RichFlag
    /** source offset of the opening tag literal, for unclosed-tag recovery */
    literalStart: number
}

const TAG_PATTERN = /<(\/?)([bi])>/gi

export function renderSubtitleRichText(text: string): RichTextSegment[] {
    const runs: MarkedRun[] = []
    const open: OpenTag[] = []
    let italic = false
    let bold = false
    let cursor = 0

    const emit = (raw: string, start: number): void => {
        if (raw !== '') {
            runs.push({ text: raw, italic, bold, start })
        }
    }

    TAG_PATTERN.lastIndex = 0
    let match: RegExpExecArray | null
    while ((match = TAG_PATTERN.exec(text)) !== null) {
        const literal = match[0]
        const flag: RichFlag = match[2].toLowerCase() === 'i' ? 'italic' : 'bold'
        const isOpen = match[1] !== '/'
        const active = flag === 'italic' ? italic : bold

        if (isOpen ? active : !active) {
            // duplicate opener or stray closer — the literal stays visible
            emit(text.slice(cursor, match.index) + literal, cursor)
        } else if (!isOpen && open[open.length - 1]?.flag !== flag) {
            // mismatched nesting (e.g. </i> while <b> is still open) — literal
            emit(text.slice(cursor, match.index) + literal, cursor)
        } else {
            emit(text.slice(cursor, match.index), cursor)
            if (isOpen) {
                open.push({ flag, literalStart: match.index })
            } else {
                open.pop()
            }
            if (flag === 'italic') {
                italic = !italic
            } else {
                bold = !bold
            }
        }
        cursor = match.index + literal.length
    }
    emit(text.slice(cursor), cursor)

    if (open.length > 0) {
        // unclosed markup: everything from the outermost unclosed opener is
        // re-emitted as one plain run — the tags never styled anything
        const recoverFrom = open[0].literalStart
        const kept = runs.filter((run) => run.start < recoverFrom)
        kept.push({ text: text.slice(recoverFrom), italic: false, bold: false, start: recoverFrom })
        runs.length = 0
        runs.push(...kept)
    }

    // merge consecutive runs sharing the same flags into final segments
    const segments: RichTextSegment[] = []
    for (const run of runs) {
        const last = segments[segments.length - 1]
        if (last && !!last.italic === run.italic && !!last.bold === run.bold) {
            last.text += run.text
        } else {
            segments.push({
                text: run.text,
                ...(run.italic ? { italic: true } : {}),
                ...(run.bold ? { bold: true } : {})
            })
        }
    }
    return segments
}
