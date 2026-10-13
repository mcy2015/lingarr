<template>
    <!-- ═══ 活跃任务行(原型 .jobrow,components spec 三组):标题+副行 /
         track / 百分比+剩余时间。整行是 queue-detail 深链。概览活跃任务卡
         与媒体详情(电影)进行中任务卡共用此形态。 -->
    <router-link
        :to="{ name: 'queue-detail', params: { id: request.id } }"
        class="hover:bg-tertiary/50 grid grid-cols-[minmax(0,1fr)_130px_74px] items-center gap-3.5 border-b border-line px-4 py-2.5 last:border-b-0"
        :data-test="`jobrow-${request.id}`">
        <div class="min-w-0">
            <b class="text-primary-content block truncate text-[13px] font-semibold" :title="request.title">
                {{ request.title }}
            </b>
            <span class="text-secondary-content block truncate text-[11.5px]">
                {{ request.sourceLanguage.toUpperCase() }} → {{ request.targetLanguage.toUpperCase() }}<template
                    v-if="request.serviceType">
                    · {{ request.serviceType }}</template
                >
            </span>
        </div>
        <TranslationProgress :progress="request.progress" :status="request.status" />
        <div class="mono text-secondary-content text-right text-xs" :title="t('common.etaHint')">
            {{ rightLine }}
        </div>
    </router-link>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import TranslationProgress from '@/cells/app/components/TranslationProgress.vue'
import { formatClock, remainingMs } from '@/cells/app/utils/eta'
import { ITranslationRequest } from '@/types'

const props = defineProps<{
    /** The in-flight (or queued) request the row summarizes. */
    request: ITranslationRequest
}>()

const { t } = useI18n()

/// Same linear estimate vocabulary as the queue rows: "38% · ~11:47",
/// degrading to the plain percentage when no start/progress is known.
const rightLine = computed(() => {
    const remaining = remainingMs(props.request.startedAt, props.request.progress)
    return remaining !== null
        ? `${props.request.progress}% · ~${formatClock(remaining)}`
        : `${props.request.progress}%`
})
</script>
