import { onUnmounted, watch, type Ref } from 'vue'
import { Hub } from '@/types'

/// Per-request SignalR group name — the only destination of per-request
/// traffic since the broadcast collapse (the global TranslationRequests
/// group carries just the ActiveTranslations summary).
export const requestGroupName = (id: number | string): string => `TranslationRequest-${id}`

/**
 * Keeps a hub connection's per-request group membership in sync with a
 * reactive list of request ids: joins groups for ids that appear, leaves
 * groups for ids that drop out, and leaves everything on component unmount.
 *
 * The hub connection is shared app-wide (useSignalR is a singleton per hub
 * url), so group membership is the only scoping mechanism — without the
 * unmount leave, a visited screen would keep receiving every request's
 * traffic forever, recreating the global-group firehose this composable
 * exists to avoid.
 *
 * Syncs are serialized through a promise chain so rapid id churn (paging,
 * refetches) can never interleave a stale join after a newer leave.
 */
export const useRequestGroups = (
    connection: Ref<Hub | null | undefined>,
    ids: Ref<readonly (number | string)[]>
): void => {
    const joined = new Set<string>()

    let chain: Promise<void> = Promise.resolve()
    const sync = (hub: Hub | null | undefined, wanted: readonly (number | string)[]): void => {
        chain = chain.then(async () => {
            if (!hub) return
            const want = new Set(wanted.map((id) => requestGroupName(id)))
            for (const group of [...joined].filter((name) => !want.has(name))) {
                joined.delete(group)
                await hub.leaveGroup({ group })
            }
            for (const group of [...want].filter((name) => !joined.has(name))) {
                joined.add(group)
                await hub.joinGroup({ group })
            }
        })
    }

    watch([connection, ids] as const, ([hub, wanted]) => sync(hub, wanted), { immediate: true })

    onUnmounted(() => {
        // leave with the still-live connection before the screen goes away
        sync(connection.value, [])
    })
}
