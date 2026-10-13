<template>
    <!-- ═══ 定时任务行级启停(screen-12 状态完备):关闭行显示「启用」,
         开启行显示「停用」;与设置·自动化批次开关同源(automation_enabled)。 -->
    <button
        type="button"
        class="focus-visible:ring-accent cursor-pointer rounded-md px-2 py-1 text-xs font-medium whitespace-nowrap transition-colors focus-visible:ring-2 focus-visible:outline-none"
        :class="
            job.enabled
                ? 'border-line text-primary-content hover:bg-tertiary border'
                : 'bg-accent text-accent-content hover:bg-accent/90 border border-accent'
        "
        :data-test="job.enabled ? 'schedule-disable' : 'schedule-enable'"
        @click="emit('toggle', job, !job.enabled)">
        {{ job.enabled ? t('settings.schedule.disable') : t('settings.schedule.enable') }}
    </button>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { IRecurringJob } from '@/types'

defineProps<{ job: IRecurringJob }>()
const emit = defineEmits<{ (e: 'toggle', job: IRecurringJob, enabled: boolean): void }>()
const { t } = useI18n()
</script>
