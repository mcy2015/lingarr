// @vitest-environment happy-dom
// P1-2 event timeline card: renders persisted events oldest-first with the
// per-type dot colour, i18n label and the server-provided detail as meta.
import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import { mount } from '@vue/test-utils'
import EventTimeline from '@/cells/queue/components/EventTimeline.vue'
import { ITranslationRequestEvent, TRANSLATION_EVENT_TYPE, TranslationEventType } from '@/types'
import en from '@/i18n/locales/en.json'

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const makeEvent = (
    id: number,
    eventType: TranslationEventType | null,
    provider: string | null,
    message: string | null,
    createdAt: string
): ITranslationRequestEvent => ({ id, status: 'InProgress', eventType, provider, message, createdAt })

describe('EventTimeline', () => {
    const events: ITranslationRequestEvent[] = [
        makeEvent(1, TRANSLATION_EVENT_TYPE.CREATED, null, null, '2026-10-13T15:33:00Z'),
        makeEvent(2, TRANSLATION_EVENT_TYPE.ENQUEUED, null, null, '2026-10-13T15:34:00Z'),
        makeEvent(3, TRANSLATION_EVENT_TYPE.PROVIDER_FAILED, 'gemini', '429 · TooManyRequests', '2026-10-13T15:36:00Z'),
        makeEvent(4, TRANSLATION_EVENT_TYPE.DEGRADED, 'openai', 'gemini -> openai', '2026-10-13T15:36:30Z'),
        makeEvent(5, TRANSLATION_EVENT_TYPE.BATCH_COMPLETED, null, 'batch 1/4 · 128/512 lines', '2026-10-13T15:37:00Z')
    ]

    const mountTimeline = () => mount(EventTimeline, {
        props: { events },
        global: { plugins: [i18n] }
    })

    it('renders every persisted event with its typed label and detail meta', () => {
        const wrapper = mountTimeline()

        const rows = wrapper.findAll('[data-test^="timeline-"]')
        expect(rows).toHaveLength(5)

        expect(rows[0].text()).toContain(en.translations.timeline.created)
        // QA P3-2: the enqueued copy names the queue; with no server message
        // (no queue-position data) no meta is appended.
        expect(rows[1].text()).toContain(en.translations.timeline.enqueued)
        expect(rows[1].text()).not.toContain('·')
        expect(rows[2].text()).toContain(en.translations.timeline.providerFailed.replace('{provider}', 'gemini'))
        expect(rows[2].text()).toContain('429 · TooManyRequests')
        expect(rows[3].text()).toContain(en.translations.timeline.degraded.replace('{provider}', 'openai'))
        expect(rows[3].text()).toContain('gemini → openai')
        expect(rows[4].text()).toContain(en.translations.timeline.batchCompleted)
        expect(rows[4].text()).toContain('batch 1/4 · 128/512 lines')
    })

    it('colours the dot by event type — failure danger, degradation warning, completion success', () => {
        const wrapper = mountTimeline()

        const dots = wrapper.findAll('[data-test^="timeline-"] span.rounded-full')
        expect(dots[2].classes()).toContain('bg-danger')
        expect(dots[3].classes()).toContain('bg-warning')
        expect(dots[4].classes()).toContain('bg-success')
    })

    it('shows the count meta in the card head', () => {
        const wrapper = mountTimeline()

        expect(wrapper.text()).toContain(en.translations.timeline.count.replace('{count}', '5'))
    })

    it('renders the empty state and forwards the reload button to refresh', async () => {
        const wrapper = mount(EventTimeline, {
            props: { events: [] },
            global: { plugins: [i18n] }
        })

        expect(wrapper.text()).toContain(en.translations.timeline.empty)

        const reload = wrapper.find('button')
        expect(reload.exists()).toBe(true)
        await reload.trigger('click')
        await new Promise((resolve) => setTimeout(resolve, 0))

        expect(wrapper.emitted('refresh')).toBeTruthy()
    })
})
