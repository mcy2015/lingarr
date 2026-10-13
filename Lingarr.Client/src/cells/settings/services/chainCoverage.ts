import { ILanguage } from '@/types'

/// Language capability of one chain member, as answered by
/// GET /api/translate/languages/{serviceType} (P1-3).
export interface ServiceLanguageTable {
    serviceType: string
    status: 'restricted' | 'unrestricted' | 'unknown'
    languages: ILanguage[]
}

/// Derived, read-only coverage of one source × target pair across the chain.
export interface CoverageRow {
    source: string
    target: string
    total: number
    available: number
    missing: string[]
    unknown: number
}

/// Two codes refer to the same language when equal (case-insensitive) or one
/// is the neutral root of the other ("en" vs "en-US") — the client-side
/// mirror of the server's GetBestMatch tiers, kept coarse on purpose.
function codeMatches(requested: string, offered: string): boolean {
    const a = requested.toLowerCase()
    const b = offered.toLowerCase()
    if (a === b) return true
    const rootA = a.split('-')[0]
    const rootB = b.split('-')[0]
    return rootA === rootB && rootA.length > 0
}

/// Whether a table can serve the pair. Unrestricted services (AI-style, no
/// language list) serve anything; restricted ones need both codes present.
export function pairSupported(
    table: ServiceLanguageTable,
    source: string,
    target: string
): boolean | null {
    if (table.status === 'unrestricted') return true
    if (table.status === 'unknown') return null
    const hasSource = table.languages.some((language) => codeMatches(source, language.code))
    const hasTarget = table.languages.some((language) => codeMatches(target, language.code))
    return hasSource && hasTarget
}

/// Coverage of every source × target combination over the chain members
/// (P1-3): "3/3" when everyone can serve, missing lists the providers that
/// cannot, unknown counts tables that could not be retrieved.
export function computeChainCoverage(
    chain: string[],
    tables: Record<string, ServiceLanguageTable>,
    sources: string[],
    targets: string[]
): CoverageRow[] {
    const rows: CoverageRow[] = []
    for (const source of sources) {
        for (const target of targets) {
            const missing: string[] = []
            let available = 0
            let unknown = 0
            for (const serviceType of chain) {
                const table = tables[serviceType]
                if (!table) {
                    unknown++
                    continue
                }
                const supported = pairSupported(table, source, target)
                if (supported === null) {
                    unknown++
                } else if (supported) {
                    available++
                } else {
                    missing.push(serviceType)
                }
            }
            rows.push({
                source: source.toUpperCase(),
                target: target.toUpperCase(),
                total: chain.length,
                available,
                missing,
                unknown
            })
        }
    }
    return rows
}
