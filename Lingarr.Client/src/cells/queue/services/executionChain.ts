import { ITranslationRequestEvent, TRANSLATION_EVENT_TYPE, TRANSLATION_STATUS } from '@/types'

export type ChainEntryState = 'failed' | 'current' | 'done' | 'unused'

export interface ChainEntry {
    service: string
    state: ChainEntryState
}

export { parseServiceChain } from '@/cells/app/utils/serviceChain'

/// Request statuses the run will never leave: once terminal, no provider is
/// "currently processing" any more — the producing chip demotes to done.
export const TERMINAL_REQUEST_STATUSES: readonly string[] = [
    TRANSLATION_STATUS.COMPLETED,
    TRANSLATION_STATUS.FAILED,
    TRANSLATION_STATUS.CANCELLED,
    TRANSLATION_STATUS.INTERRUPTED
]

export const isTerminalRequestStatus = (status: string | null | undefined): boolean =>
    !!status && TERMINAL_REQUEST_STATUSES.includes(status)

/// Derives the per-service chip state of the execution chain (P1-2) from the
/// request's persisted event timeline:
/// - failed: a ProviderFailed event names the service — checked first, so a
///   provider that failed is never masked as current by a stale
///   request.serviceType;
/// - current: the provider of the most recent Degraded event when the walk
///   degraded (events outrank the request's recorded service, which
///   historically stayed the primary after a fallback completed the run);
///   otherwise the request's recorded service — but only while the task may
///   still produce output (walk-through fix D1: a terminal task demotes the
///   producer to done so "Currently processing" never outlives the run);
/// - unused: everything the walk never needed.
export function deriveChainState(
    chain: string[],
    events: ITranslationRequestEvent[],
    currentService?: string | null,
    taskStatus?: string | null
): ChainEntry[] {
    const failedProviders = new Set(
        events
            .filter((event) => event.eventType === TRANSLATION_EVENT_TYPE.PROVIDER_FAILED && event.provider)
            .map((event) => event.provider as string)
    )

    // Degradation is authoritative: the walk fell back to that provider, so it
    // is the current producer regardless of what request.serviceType says.
    const lastDegraded = [...events]
        .reverse()
        .find((event) => event.eventType === TRANSLATION_EVENT_TYPE.DEGRADED && event.provider)
    const current: string | null = (lastDegraded?.provider as string) ?? currentService ?? null
    const finished = isTerminalRequestStatus(taskStatus)

    return chain.map((service) => {
        if (failedProviders.has(service)) {
            return { service, state: 'failed' as const }
        }
        if (service === current) {
            return { service, state: finished ? ('done' as const) : ('current' as const) }
        }
        return { service, state: 'unused' as const }
    })
}
