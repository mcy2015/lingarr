import { onUnmounted, ref } from 'vue'
import { Hub, ILineProofread, ILineTranslated, IRequestProgress } from '@/types'
import { useSignalR } from '@/cells/app/composables/useSignalR'

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
 * Encapsulates connecting to the TranslationRequests hub, joining the group and
 * (un)subscribing the detail handlers. connect() is explicit so the caller
 * controls when the socket comes up relative to its own data fetching; handler
 * cleanup on component unmount is handled here.
 */
export const useTranslationDetailHub = () => {
    const signalR = useSignalR()
    const connection = ref<Hub | null>(null)
    let handlers: TranslationDetailHubHandlers | null = null

    const connect = async (detailHandlers: TranslationDetailHubHandlers): Promise<void> => {
        handlers = detailHandlers
        connection.value = await signalR.connect(HUB_NAME, HUB_URL)
        await connection.value.joinGroup({ group: HUB_NAME })
        connection.value.on('RequestProgress', detailHandlers.onRequestProgress)
        connection.value.on('LineTranslated', detailHandlers.onLineTranslated)
        connection.value.on('LineProofread', detailHandlers.onLineProofread)
    }

    onUnmounted(() => {
        if (!connection.value || !handlers) {
            return
        }
        connection.value.off('RequestProgress', handlers.onRequestProgress)
        connection.value.off('LineTranslated', handlers.onLineTranslated)
        connection.value.off('LineProofread', handlers.onLineProofread)
    })

    return { connection, connect }
}
