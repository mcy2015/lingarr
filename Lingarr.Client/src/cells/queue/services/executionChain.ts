import { ITranslationRequestEvent, TRANSLATION_EVENT_TYPE } from '@/types'

export type ChainEntryState = 'failed' | 'current' | 'unused'

export interface ChainEntry {
    service: string
    state: ChainEntryState
}

export { parseServiceChain } from '@/cells/app/utils/serviceChain'

/// Derives the per-service chip state of the execution chain (P1-2) from the
/// request's persisted event timeline:
/// - failed: a ProviderFailed event names the service (and it is not the one
///   currently producing);
/// - current: the request's recorded service once terminal, else the provider
///   of the most recent Degraded event (the fallback that took over);
/// - unused: everything the walk never needed.
export function deriveChainState(
    chain: string[],
    events: ITranslationRequestEvent[],
    currentService?: string | null
): ChainEntry[] {
    const failedProviders = new Set(
        events
            .filter((event) => event.eventType === TRANSLATION_EVENT_TYPE.PROVIDER_FAILED && event.provider)
            .map((event) => event.provider as string)
    )

    let current: string | null = currentService ?? null
    if (!current) {
        const lastDegraded = [...events]
            .reverse()
            .find((event) => event.eventType === TRANSLATION_EVENT_TYPE.DEGRADED && event.provider)
        current = lastDegraded?.provider ?? null
    }

    return chain.map((service) => {
        if (service === current) {
            return { service, state: 'current' as const }
        }
        if (failedProviders.has(service)) {
            return { service, state: 'failed' as const }
        }
        return { service, state: 'unused' as const }
    })
}
