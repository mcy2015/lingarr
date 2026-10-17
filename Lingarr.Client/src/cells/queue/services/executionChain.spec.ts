// P1-2 execution chain derivation: chip states come from the persisted event
// timeline — failed providers get ✗, the producing provider gets ▸, the rest
// stay dim. Degraded and clean histories must both derive correctly.
// Walk-through fix D1: a terminal task demotes the producing chip from
// current to done — "Currently processing" must not outlive the run.
import { describe, expect, it } from 'vitest'
import {
    deriveChainState,
    isTerminalRequestStatus,
    parseServiceChain
} from '@/cells/queue/services/executionChain'
import {
    ITranslationRequestEvent,
    TRANSLATION_EVENT_TYPE,
    TRANSLATION_STATUS,
    TranslationEventType
} from '@/types'

const event = (eventType: TranslationEventType | null, provider?: string | null): ITranslationRequestEvent => ({
    id: Math.random(),
    status: 'InProgress',
    eventType,
    provider: provider ?? null,
    message: null,
    createdAt: '2026-10-13T15:36:00Z'
})

describe('parseServiceChain', () => {
    it('parses the JSON array form', () => {
        expect(parseServiceChain('["gemini","openai","ollama"]')).toEqual(['gemini', 'openai', 'ollama'])
    })

    it('accepts the legacy single-value form and empty values', () => {
        expect(parseServiceChain('openai')).toEqual(['openai'])
        expect(parseServiceChain(null)).toEqual([])
        expect(parseServiceChain('[]')).toEqual([])
        expect(parseServiceChain('["openai",}')).toEqual([]) // malformed JSON array
    })
})

describe('deriveChainState', () => {
    const chain = ['gemini', 'openai', 'ollama']

    it('marks the failed provider failed even when it is also the current service', () => {
        // the failure check runs first: a provider with a ProviderFailed event
        // is never masked as current by a stale request.serviceType
        const entries = deriveChainState(
            chain,
            [event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini'), event(TRANSLATION_EVENT_TYPE.STARTED)],
            'gemini'
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'failed' },
            { service: 'openai', state: 'unused' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('marks the failed provider and the degradation target on a fallback run', () => {
        const entries = deriveChainState(
            chain,
            [
                event(TRANSLATION_EVENT_TYPE.STARTED),
                event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini'),
                event(TRANSLATION_EVENT_TYPE.DEGRADED, 'openai'),
                event(TRANSLATION_EVENT_TYPE.BATCH_COMPLETED)
            ],
            null
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'failed' },
            { service: 'openai', state: 'current' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('keeps the primary current when nothing failed', () => {
        // in-progress without persisted serviceType: the last Degraded provider
        // is absent, so no chip is guessed current until lines carry a service
        const entries = deriveChainState(
            chain,
            [event(TRANSLATION_EVENT_TYPE.CREATED), event(TRANSLATION_EVENT_TYPE.STARTED)],
            null
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'unused' },
            { service: 'openai', state: 'unused' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('prefers the last Degraded provider over the request service on a degraded completion', () => {
        // the run degraded to openai and completed; older rows persisted the
        // primary (gemini) as request.serviceType — the Degraded event is
        // authoritative, so the fallback shows current and the failed primary
        // shows failed
        const entries = deriveChainState(
            chain,
            [
                event(TRANSLATION_EVENT_TYPE.STARTED),
                event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini'),
                event(TRANSLATION_EVENT_TYPE.DEGRADED, 'openai'),
                event(TRANSLATION_EVENT_TYPE.COMPLETED)
            ],
            'gemini'
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'failed' },
            { service: 'openai', state: 'current' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('uses the request service when the run never degraded', () => {
        // no Degraded event: the request's recorded service wins
        const entries = deriveChainState(
            chain,
            [event(TRANSLATION_EVENT_TYPE.STARTED), event(TRANSLATION_EVENT_TYPE.COMPLETED)],
            'openai'
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'unused' },
            { service: 'openai', state: 'current' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('derives current from the latest translating line when no service recorded', () => {
        const entries = deriveChainState(
            chain,
            [event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini'), event(TRANSLATION_EVENT_TYPE.DEGRADED, 'deepl')],
            'deepl'
        )

        // deepl is not on the configured chain — only chips for chain members
        expect(entries.map((entry) => entry.state)).toEqual(['failed', 'unused', 'unused'])
    })

    // ── walk-through fix D1: terminal tasks demote current → done ──
    it('demotes the producing chip to done on a completed task', () => {
        const entries = deriveChainState(
            chain,
            [event(TRANSLATION_EVENT_TYPE.STARTED), event(TRANSLATION_EVENT_TYPE.COMPLETED)],
            'openai',
            TRANSLATION_STATUS.COMPLETED
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'unused' },
            { service: 'openai', state: 'done' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('marks the degradation target done on a degraded completion', () => {
        // events outrank the stale request.serviceType; the terminal status
        // then demotes the fallback producer from current to done
        const entries = deriveChainState(
            chain,
            [
                event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini'),
                event(TRANSLATION_EVENT_TYPE.DEGRADED, 'openai'),
                event(TRANSLATION_EVENT_TYPE.COMPLETED)
            ],
            'gemini',
            TRANSLATION_STATUS.COMPLETED
        )

        expect(entries).toEqual([
            { service: 'gemini', state: 'failed' },
            { service: 'openai', state: 'done' },
            { service: 'ollama', state: 'unused' }
        ])
    })

    it('demotes the producing chip on every terminal status, never masking a failure', () => {
        for (const status of [
            TRANSLATION_STATUS.FAILED,
            TRANSLATION_STATUS.CANCELLED,
            TRANSLATION_STATUS.INTERRUPTED
        ]) {
            // the failed check runs first: a provider with a ProviderFailed
            // event stays failed even though it is also the producing service
            const entries = deriveChainState(
                chain,
                [event(TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'openai')],
                'openai',
                status
            )

            expect(entries.map((entry) => entry.state)).toEqual(['unused', 'failed', 'unused'])
        }
    })

    it('keeps the chip current while the task may still produce output', () => {
        for (const status of [
            TRANSLATION_STATUS.INPROGRESS,
            TRANSLATION_STATUS.PAUSED,
            TRANSLATION_STATUS.PENDING,
            undefined
        ]) {
            const entries = deriveChainState(
                chain,
                [event(TRANSLATION_EVENT_TYPE.STARTED)],
                'openai',
                status
            )

            expect(entries.map((entry) => entry.state)).toEqual(['unused', 'current', 'unused'])
        }
    })
})

describe('isTerminalRequestStatus', () => {
    it('recognizes exactly the terminal statuses', () => {
        for (const status of [
            TRANSLATION_STATUS.COMPLETED,
            TRANSLATION_STATUS.FAILED,
            TRANSLATION_STATUS.CANCELLED,
            TRANSLATION_STATUS.INTERRUPTED
        ]) {
            expect(isTerminalRequestStatus(status)).toBe(true)
        }
        for (const status of [
            TRANSLATION_STATUS.INPROGRESS,
            TRANSLATION_STATUS.PAUSED,
            TRANSLATION_STATUS.PENDING,
            null,
            undefined,
            ''
        ]) {
            expect(isTerminalRequestStatus(status)).toBe(false)
        }
    })
})
