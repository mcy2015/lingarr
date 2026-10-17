<template>
    <!-- 执行链行(原型 screen-09 .fb-chain,P1-2):service_type 链 chips,
         状态从事件时间线推导 —— ✗ 已失败 / ▸ 当前(仅运行中,D1) /
         ✓ 已产出(终态) / dim 未用。 -->
    <div v-if="entries.length > 0" class="border-line mt-4 flex flex-wrap items-center gap-2 rounded-md border p-3">
        <span class="text-secondary-content text-xs font-semibold">{{ t('translations.chain.label') }}</span>
        <span
            v-for="entry in entries"
            :key="entry.service"
            :data-test="`chain-${entry.service}`"
            class="mono rounded-[3px] border px-2 py-0.5 text-[11.5px]"
            :class="chipClass(entry)"
            :title="chipTitle(entry)">
            {{ entry.service }}
            <span v-if="entry.state === 'failed'"> ✗</span>
            <span v-else-if="entry.state === 'current'"> ▸</span>
            <span v-else-if="entry.state === 'done' && isCompleted"> ✓</span>
        </span>
    </div>
    <div v-else class="text-secondary-content/60 mt-4 text-xs">
        {{ t('translations.chain.empty') }}
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { TRANSLATION_STATUS } from '@/types'
import { ChainEntry } from '@/cells/queue/services/executionChain'

const props = defineProps<{
    entries: ChainEntry[]
    /** Overall request status: a terminal run demotes "current" chips to done. */
    taskStatus?: string | null
}>()

const { t } = useI18n()

const isCompleted = computed(() => props.taskStatus === TRANSLATION_STATUS.COMPLETED)

/// Terminal-but-not-completed runs keep the producing chip visually calm; its
/// tooltip then names the terminal status instead of claiming completion
/// (existing keys only — no hard-coded copy, per the walk-through rules).
const TERMINAL_TITLE_KEYS: Record<string, string> = {
    [TRANSLATION_STATUS.FAILED]: 'common.translationStatus.failed',
    [TRANSLATION_STATUS.CANCELLED]: 'common.translationStatus.cancelled',
    [TRANSLATION_STATUS.INTERRUPTED]: 'common.translationStatus.interrupted'
}

const chipClass = (entry: ChainEntry): string => {
    switch (entry.state) {
        case 'failed':
            return 'border-danger/50 bg-danger/10 text-danger'
        case 'current':
            return 'border-accent/60 bg-accent/10 text-accent'
        case 'done':
            return isCompleted.value
                ? 'border-success/40 bg-success/10 text-success'
                : 'border-line bg-tertiary/50 text-secondary-content'
        default:
            return 'border-line bg-tertiary/50 text-secondary-content/60'
    }
}

const chipTitle = (entry: ChainEntry): string => {
    switch (entry.state) {
        case 'failed':
            return t('translations.chain.state.failed')
        case 'current':
            return t('translations.chain.state.current')
        case 'done': {
            const terminalKey = TERMINAL_TITLE_KEYS[props.taskStatus ?? '']
            return terminalKey ? t(terminalKey) : t('translations.detail.completed')
        }
        default:
            return t('translations.chain.state.unused')
    }
}
</script>
