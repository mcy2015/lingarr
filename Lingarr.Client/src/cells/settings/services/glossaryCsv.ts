import { IGlossaryBulkEntry, ITranslationGlossary } from '@/types'

export interface IGlossaryCsvParseResult {
    entries: IGlossaryBulkEntry[]
    /// malformed rows: wrong field count, blank terms, unknown mode,
    /// unterminated quotes
    skipped: number
}

/// Parses one CSV line under the standard quoting rules: fields are
/// comma-separated, a field may be wrapped in double quotes to keep commas,
/// and a literal quote inside a quoted field is doubled (""). Returns null
/// for malformed lines (stray/unterminated quotes) so callers can skip them.
function parseCsvLine(line: string): string[] | null {
    const fields: string[] = []
    let field = ''
    let inQuotes = false
    let fieldStarted = false

    for (let i = 0; i < line.length; i++) {
        const char = line[i]
        if (inQuotes) {
            if (char === '"') {
                if (line[i + 1] === '"') {
                    field += '"'
                    i++
                } else {
                    inQuotes = false
                }
            } else {
                field += char
            }
        } else if (char === '"' && !fieldStarted) {
            inQuotes = true
            fieldStarted = true
        } else if (char === ',') {
            fields.push(field)
            field = ''
            fieldStarted = false
        } else if (char === '"') {
            // quote in the middle of an unquoted field — malformed
            return null
        } else {
            field += char
            fieldStarted = true
        }
    }

    if (inQuotes) return null
    fields.push(field)
    return fields
}

/// Mode column values accepted from a glossary CSV, case-insensitively:
/// locked/锁定 → locked term, context/上下文 (or missing) → advisory.
/// Any other value makes the row malformed.
function parseMode(raw: string | undefined): 'locked' | 'context' | null {
    const mode = (raw ?? '').trim().toLowerCase()
    if (mode === '' || mode === 'context' || mode === '上下文') return 'context'
    if (mode === 'locked' || mode === '锁定') return 'locked'
    return null
}

/// Glossary CSV import parser (prototype screen-06 C「导入 CSV」):
/// columns are source,target[,mode]; the header row is optional (detected
/// by a literal "source" first cell, case-insensitive). Malformed rows are
/// skipped and counted — the caller reports them in the toast.
export function parseGlossaryCsv(text: string): IGlossaryCsvParseResult {
    const result: IGlossaryCsvParseResult = { entries: [], skipped: 0 }
    const lines = text.split(/\r\n|\n|\r/)

    let sawHeader = false
    for (const line of lines) {
        if (line.trim() === '') continue

        const fields = parseCsvLine(line)
        if (!fields) {
            result.skipped++
            continue
        }

        // optional header: first cell literally reads "source"
        if (!sawHeader && fields[0].trim().toLowerCase() === 'source') {
            sawHeader = true
            continue
        }

        const source = (fields[0] ?? '').trim()
        const target = (fields[1] ?? '').trim()
        const mode = parseMode(fields[2])

        // a row with fewer than two fields has a blank term and is caught
        // by the emptiness checks
        if (!source || !target || mode === null) {
            result.skipped++
            continue
        }

        result.entries.push({ sourceTerm: source, targetTerm: target, matchMode: mode })
    }

    return result
}

/// Quotes one CSV field when the import parser would otherwise misread it:
/// a field containing a comma, quote or line break is wrapped in double
/// quotes, with inner quotes doubled — the standard rules mirrored from
/// parseCsvLine above.
function escapeCsvField(field: string): string {
    if (
        field.indexOf(',') === -1 &&
        field.indexOf('"') === -1 &&
        field.indexOf('\r') === -1 &&
        field.indexOf('\n') === -1
    ) {
        return field
    }
    return `"${field.replace(/"/g, '""')}"`
}

/// Builds the glossary CSV (R9a「导出 CSV」) as the exact symmetric
/// counterpart of parseGlossaryCsv: a UTF-8 BOM prefix (Excel opens CJK
/// terms directly), the source,target,mode header, and one row per entry
/// with the canonical locked/context mode spelling. An empty glossary
/// yields the header row only, and the output re-imports losslessly.
export function buildGlossaryCsv(
    entries: Pick<ITranslationGlossary, 'sourceTerm' | 'targetTerm' | 'isLocked'>[]
): string {
    const rows = entries.map(
        (entry) =>
            `${escapeCsvField(entry.sourceTerm)},${escapeCsvField(entry.targetTerm)},` +
            `${entry.isLocked ? 'locked' : 'context'}`
    )
    return `\uFEFFsource,target,mode\r\n${rows.join('\r\n')}${rows.length > 0 ? '\r\n' : ''}`
}
