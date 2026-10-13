<template>
    <!-- ═══ 定时任务下钻屏(原型 screen-12 裁定② 轻改):pb-head(← 系统 +
         Hangfire 副行 + 查看任务队列)+ cron 列 + 状态 chip 化。启停/新建/
         队列深度不做(服务端无端点);触发现有 TriggerJob 逻辑保留。 -->
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <PbHead
            :back-to="{ name: 'settings-system' }"
            :back-label="t('settings.menu.groups.system')"
            :title="t('settings.schedule.heading')"
            :sub="subLine">
            <template #actions>
                <ReloadComponent @toggle:update="scheduleStore.fetchRecurringJobs" />
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                    data-test="schedule-view-queue"
                    @click="router.push({ name: 'queue' })">
                    {{ t('settings.schedule.viewQueue') }}
                </button>
            </template>
        </PbHead>

        <div
            class="border-line bg-secondary shell-scroll min-h-0 flex-1 overflow-y-auto rounded-md border">
            <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                <div class="px-4 py-2">{{ t('settings.schedule.jobName') }}</div>
                <div class="px-4 py-2">{{ t('settings.schedule.cron') }}</div>
                <div class="px-4 py-2">{{ t('settings.schedule.lastExecution') }}</div>
                <div class="px-4 py-2">{{ t('settings.schedule.nextExecution') }}</div>
                <div class="px-4 py-2">{{ t('settings.schedule.state') }}</div>
                <div class="px-4 py-2">{{ t('settings.schedule.actions') }}</div>
            </div>

            <div
                v-for="job in jobs"
                :key="job.id"
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b py-3 md:grid md:grid-cols-12 md:gap-0 md:py-0">
                <div class="flex w-full items-center gap-2 md:col-span-4 md:w-auto md:px-4 md:py-2">
                    <span class="mono min-w-0 flex-1 truncate md:flex-none" :title="job.id">
                        {{ job.id }}
                    </span>
                    <span class="ml-auto flex-none md:hidden">
                        <TriggerJob
                            :title="t('settings.schedule.run')"
                            @toggle:trigger="scheduleStore.startJob(job.id)" />
                    </span>
                </div>
                <div class="mono text-secondary-content md:col-span-2 md:px-4 md:py-2">
                    {{ job.cron }}
                </div>
                <div
                    :class="job.lastExecution ? 'flex' : 'hidden md:flex'"
                    class="items-center gap-2 md:col-span-2 md:px-4 md:py-2">
                    <span v-if="job.lastExecution" class="mono text-secondary-content text-xs">
                        {{ formatDateTime(job.lastExecution) }}
                    </span>
                    <span
                        class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                        {{ t('settings.schedule.lastRun') }}
                    </span>
                </div>
                <div
                    :class="job.nextExecution ? 'flex' : 'hidden md:flex'"
                    class="items-center gap-2 md:col-span-2 md:px-4 md:py-2">
                    <span v-if="job.nextExecution" class="mono text-secondary-content text-xs">
                        {{ formatDateTime(job.nextExecution) }}
                    </span>
                    <span
                        class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                        {{ t('settings.schedule.nextRun') }}
                    </span>
                </div>
                <div class="flex items-center md:col-span-1 md:px-4 md:py-2">
                    <!-- 状态 chip 化:运行中 busy / 上次成功 ok / 其他 dim -->
                    <span
                        class="mono rounded-[3px] border px-1.5 py-px text-[11px]"
                        :class="stateChipClass(job)"
                        :title="job.currentState">
                        {{ stateChipLabel(job) }}
                    </span>
                </div>
                <div
                    class="hidden md:col-span-1 md:flex md:items-center md:justify-end md:px-4 md:py-2">
                    <TriggerJob
                        :title="t('settings.schedule.run')"
                        @toggle:trigger="scheduleStore.startJob(job.id)" />
                </div>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Hub, IRecurringJob } from '@/types'
import { formatDateTime } from '@/cells/app/utils/date'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import PbHead from '@/cells/app/components/PbHead.vue'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'

const scheduleStore = useScheduleStore()
const signalR = useSignalR()
const router = useRouter()
const { t } = useI18n()
const hubConnection = ref<Hub>()
const jobs = computed(() => scheduleStore.getRecurringJobs)

/// mono 副行只用真实数据:任务未加载时仅「Hangfire recurring」,加载后
/// 附任务数与队列数(并发上限无端点/设置可读,不展示)。
const subLine = computed(() => {
    if (jobs.value.length === 0) {
        return t('settings.schedule.subLineBase')
    }
    const queues = new Set(jobs.value.map((job) => job.queue)).size
    return t('settings.schedule.subLine', { jobs: jobs.value.length, queues })
})

const stateChipClass = (job: IRecurringJob): string => {
    if (job.isCurrentlyRunning) {
        return 'text-info-content border-info/40 bg-info/20'
    }
    if (job.lastJobState === 'Succeeded') {
        return 'text-success-content border-success/40 bg-success/50'
    }
    return 'text-secondary-content border-line bg-tertiary opacity-55'
}

const stateChipLabel = (job: IRecurringJob): string => {
    if (job.isCurrentlyRunning) {
        return t('settings.schedule.stateRunning')
    }
    if (job.lastJobState === 'Succeeded') {
        return t('settings.schedule.stateSucceeded')
    }
    if (!job.lastJobState) {
        return t('settings.schedule.stateNone')
    }
    return job.lastJobState
}

const onJobStateUpdated = (jobId: string, state: string) => {
    const job = jobs.value.find((job) => job.id === jobId)
    if (job) {
        job.currentState = state
    }
}

onMounted(async () => {
    await scheduleStore.fetchRecurringJobs()
    hubConnection.value = await signalR.connect('JobProgress', '/signalr/JobProgress')
    await hubConnection.value.joinGroup({ group: 'JobProgress' })

    hubConnection.value.on('JobStateUpdated', onJobStateUpdated)
})

onUnmounted(async () => {
    hubConnection.value?.off('JobStateUpdated', onJobStateUpdated)
})
</script>
