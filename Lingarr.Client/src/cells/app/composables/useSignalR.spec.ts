// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createSignalRStore } from '@/cells/app/composables/useSignalR'

const connection = vi.hoisted(() => ({
    start: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    off: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn()
}))

vi.mock('@microsoft/signalr', () => ({
    HubConnectionBuilder: class {
        withUrl() {
            return this
        }
        configureLogging() {
            return this
        }
        withAutomaticReconnect() {
            return this
        }
        build() {
            return connection
        }
    },
    LogLevel: { None: 0 }
}))

describe('useSignalR connect', () => {
    beforeEach(() => {
        vi.clearAllMocks()
    })

    it('concurrent connect calls share one start and await it before group joins', async () => {
        let releaseStart: (() => void) | undefined
        connection.start.mockImplementation(
            () =>
                new Promise<void>((resolve) => {
                    releaseStart = resolve
                })
        )

        const store = createSignalRStore()
        const first = store.connect('requests', '/hubs/requests')
        const second = store.connect('requests', '/hubs/requests')

        await Promise.resolve()
        await Promise.resolve()

        // start is pending: no hub may have been handed out, no invoke allowed
        expect(connection.start).toHaveBeenCalledOnce()
        expect(connection.invoke).not.toHaveBeenCalled()

        releaseStart?.()
        const [hubA, hubB] = await Promise.all([first, second])

        await hubA.joinGroup({ group: 'request-1' })
        await hubB.joinGroup({ group: 'request-2' })
        expect(connection.invoke).toHaveBeenCalledWith('JoinGroup', { group: 'request-1' })
        expect(connection.invoke).toHaveBeenCalledWith('JoinGroup', { group: 'request-2' })
    })

    it('sequential connect after establishment does not restart the connection', async () => {
        connection.start.mockResolvedValue(undefined)

        const store = createSignalRStore()
        await store.connect('requests', '/hubs/requests')
        await store.connect('requests', '/hubs/requests')

        expect(connection.start).toHaveBeenCalledOnce()
        expect(store.state.hubs.requests?.isConnected).toBe(true)
    })

    it('a failed start records the error and still resolves the awaiter', async () => {
        const failure = new Error('socket refused')
        connection.start.mockRejectedValue(failure)
        const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined)

        const store = createSignalRStore()
        const hub = await store.connect('requests', '/hubs/requests')

        expect(store.state.hubs.requests?.isConnected).toBe(false)
        expect(store.state.hubs.requests?.lastError).toBe(failure)
        // the pending promise must not reject — callers keep a usable hub shell:
        // group joins are still attempted (and swallowed by the invoke guard)
        // instead of throwing out of the awaiter.
        await hub.joinGroup({ group: 'g' })
        expect(connection.invoke).toHaveBeenCalledWith('JoinGroup', { group: 'g' })
        expect(logged).toHaveBeenCalled()
        logged.mockRestore()
    })
})
