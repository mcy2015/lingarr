import {
    IActiveTranslation,
    ILineProofread,
    ILineTranslated,
    IRequestProgress,
    ISettings
} from '@/types'
import type { HubConnection as SignalRHubConnection } from '@microsoft/signalr'

export interface SignalRStore {
    state: SignalRState
    connect: (hubName: string, url: string) => Promise<Hub>
}

export interface SignalRState {
    hubs: Record<string, HubConnection>
}

interface HubConnection {
    connection: SignalRHubConnection
    isConnected: boolean
    lastError: Error | null
}

export type EventCallbacks = {
    GroupCompleted: (group: string) => void
    SettingUpdate: (setting: { key: keyof ISettings; value: string }) => void
    RequestProgress: (requestProgress: IRequestProgress) => void
    ActiveTranslations: (activeTranslations: IActiveTranslation[]) => void
    JobStateUpdated: (jobId: string, state: string) => void
    /// Server batches per-line translation traffic: one array message per
    /// 50 lines / 50ms window (terminal states flush the tail). The retired
    /// per-line LineTranslated event is gone from the wire contract.
    LineTranslatedBatch: (lines: ILineTranslated[]) => void
    LineProofread: (line: ILineProofread) => void
}

export interface Hub {
    joinGroup: (groupName: { group: string }) => Promise<void>
    leaveGroup: (groupName: { group: string }) => Promise<void>
    send: (event: string, ...args: unknown[]) => Promise<void>
    on<K extends keyof EventCallbacks>(event: K, callback: EventCallbacks[K]): void
    off<K extends keyof EventCallbacks>(event: K, callback: EventCallbacks[K]): void
}
