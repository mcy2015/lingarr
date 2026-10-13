<template>
    <!-- 事件时间线卡(原型 screen-09,P1-2):时间 + 色点 + 文案 + meta。
         只渲染真实落库的事件,不构造未来事件。 -->
    <div class="rounded-md border border-line bg-secondary flex min-h-0 max-h-[420px] flex-col">
        <div class="flex shrink-0 items-center gap-2.5 border-b border-line px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('translations.timeline.title') }}</h2>
            <span class="mono text-secondary-content ml-auto text-xs">
                {{ t('translations.timeline.count', { count: events.length }) }}
            </span>
            <ReloadComponent @toggle:update="() => emit('refresh')" />
        </div>
        <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
            <p
                v-if="events.length === 0"
                class="text-secondary-content/60 my-auto px-4 py-6 text-center text-sm">
                {{ t('translations.timeline.empty') }}
            </p>
            <div
                v-for="event in events"
                :key="event.id"
                class="flex gap-3 border-b border-line px-4 py-2 text-[12.5px] last:border-b-0"
                :data-test="`timeline-${event.id}`">
                <time class="mono text-secondary-content shrink-0 pt-0.5 text-[11px]">
                    {{ formatTime(event.createdAt) }}
                </time>
                <span class="mt-1.5 h-[7px] w-[7px] shrink-0 rounded-full" :class="dotClass(event)"></span>
                <span class="min-w-0 flex-1">
                    {{ eventLabel(event) }}
                    <span v-if="eventMessage(event)" class="text-secondary-content mono text-[11px]">
                        · {{ eventMessage(event) }}
                    </span>
                </span>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import { formatDegradationEdge } from '@/cells/app/utils/degradationEdge'
import { ITranslationRequestEvent, TRANSLATION_EVENT_TYPE } from '@/types'

const props = defineProps<{
    events: ITranslationRequestEvent[]
}>()

const emit = defineEmits(['refresh'])
const { t, locale } = useI18n()

const dotClass = (event: ITranslationRequestEvent): string => {
    switch (event.eventType) {
        case TRANSLATION_EVENT_TYPE.PROVIDER_FAILED:
            return 'bg-danger'
        case TRANSLATION_EVENT_TYPE.DEGRADED:
            return 'bg-warning'
        case TRANSLATION_EVENT_TYPE.BATCH_COMPLETED:
        case TRANSLATION_EVENT_TYPE.WRITEBACK:
        case TRANSLATION_EVENT_TYPE.COMPLETED:
            return 'bg-success'
        case TRANSLATION_EVENT_TYPE.STARTED:
            return 'bg-accent'
        case TRANSLATION_EVENT_TYPE.FAILED:
            return 'bg-danger'
        default:
            return 'bg-secondary-content/40'
    }
}

const eventLabel = (event: ITranslationRequestEvent): string => {
    switch (event.eventType) {
        case TRANSLATION_EVENT_TYPE.CREATED:
            return t('translations.timeline.created')
        case TRANSLATION_EVENT_TYPE.ENQUEUED:
            return t('translations.timeline.enqueued')
        case TRANSLATION_EVENT_TYPE.STARTED:
            return t('translations.timeline.started')
        case TRANSLATION_EVENT_TYPE.PROVIDER_FAILED:
            return t('translations.timeline.providerFailed', { provider: event.provider ?? '' })
        case TRANSLATION_EVENT_TYPE.DEGRADED:
            return t('translations.timeline.degraded', { provider: event.provider ?? '' })
        case TRANSLATION_EVENT_TYPE.BATCH_COMPLETED:
            return t('translations.timeline.batchCompleted')
        case TRANSLATION_EVENT_TYPE.COMPLETED:
            return t('translations.timeline.completed')
        case TRANSLATION_EVENT_TYPE.FAILED:
            return t('translations.timeline.failed')
        case TRANSLATION_EVENT_TYPE.WRITEBACK:
            return t('translations.timeline.writeback')
        default: {
            const camel = event.status.charAt(0).toLowerCase() + event.status.slice(1)
            return t(`common.translationStatus.${camel}`)
        }
    }
}

/// The server message carries the concrete detail (status code, degradation
/// edge, batch counters, output path) — surfaced as the row meta.
const eventMessage = (event: ITranslationRequestEvent): string | null => {
    if (event.eventType === TRANSLATION_EVENT_TYPE.DEGRADED && event.message) {
        return formatDegradationEdge(event.message)
    }
    return event.message ?? null
}

const formatTime = (createdAt: string): string =>
    new Date(createdAt).toLocaleString(locale.value, {
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit'
    })
</script>
