import { onUnmounted, ref } from 'vue'
import { Hub, ILineProofread, ILineTranslated, IRequestProgress } from '@/types'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { requestGroupName } from '@/cells/app/composables/useRequestGroups'

const HUB_NAME = 'TranslationRequests'
const HUB_URL = '/signalr/TranslationRequests'

export interface TranslationDetailHubHandlers {
    onRequestProgress: (progress: IRequestProgress) => void
    onLineTranslated: (line: ILineTranslated) => void
    onLineProofread: (line: ILineProofread) => void
}

/**
 * Live-progress / line-streaming wiring for the translation detail page.
 *
 * Encapsulates connecting to the TranslationRequests hub, joining the
 * request's OWN group (per-request traffic never touches the global group —
 * broadcast collapse) and (un)subscribing the detail handlers. Line traffic
 * arrives as LineTranslatedBatch array messages; the per-item handler is
 * fanned out here so callers keep their single-line contract. connect() is
 * explicit so the caller controls when the socket comes up relative to its
 * own data fetching; handler cleanup and the group leave on component
 * unmount are handled here.
 */
export const useTranslationDetailHub = () => {
    const signalR = useSignalR()
    const connection = ref<Hub | null>(null)
    let handlers: TranslationDetailHubHandlers | null = null
    let batchHandler: ((lines: ILineTranslated[]) => void) | null = null
    let group: string | null = null

    const connect = async (
        requestId: number | string,
        detailHandlers: TranslationDetailHubHandlers
    ): Promise<void> => {
        handlers = detailHandlers
        connection.value = await signalR.connect(HUB_NAME, HUB_URL)
        group = requestGroupName(requestId)
        await connection.value.joinGroup({ group })
        connection.value.on('RequestProgress', detailHandlers.onRequestProgress)
        // one array message per 50 lines / 50ms window → fan out per item
        batchHandler = (lines: ILineTranslated[]) => {
            for (const line of lines) {
                detailHandlers.onLineTranslated(line)
            }
        }
        connection.value.on('LineTranslatedBatch', batchHandler)
        connection.value.on('LineProofread', detailHandlers.onLineProofread)
    }

    onUnmounted(() => {
        if (!connection.value || !handlers) {
            return
        }
        connection.value.off('RequestProgress', handlers.onRequestProgress)
        if (batchHandler) {
            connection.value.off('LineTranslatedBatch', batchHandler)
        }
        connection.value.off('LineProofread', handlers.onLineProofread)
        // the shared connection must not keep this request's traffic after
        // the detail screen is gone
        if (group) {
            void connection.value.leaveGroup({ group })
        }
    })

    return { connection, connect }
}
