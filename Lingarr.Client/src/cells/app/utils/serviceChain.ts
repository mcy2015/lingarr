/// Parses the service_type setting (single value or JSON array) into the
/// ordered fallback chain; first entry is the primary, the rest fallbacks.
/// Shared by the queue execution chain (P1-2) and the settings language
/// policy card (P1-3), hence the app cell.
export function parseServiceChain(raw: string | undefined | null): string[] {
    if (!raw) return []
    const trimmed = raw.trim()
    if (!trimmed.startsWith('[')) {
        return trimmed ? [trimmed] : []
    }
    try {
        const parsed = JSON.parse(trimmed) as unknown
        if (Array.isArray(parsed)) {
            return parsed.filter((entry): entry is string => typeof entry === 'string' && entry.length > 0)
        }
    } catch {
        // malformed setting — fall through to an empty chain
    }
    return []
}
