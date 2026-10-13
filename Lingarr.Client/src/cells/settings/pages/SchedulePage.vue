<template>
    <div class="w-full">
        <div class="bg-tertiary flex flex-wrap items-center justify-end gap-2 p-4">
            <ReloadComponent @toggle:update="scheduleStore.fetchRecurringJobs" />
        </div>

        <div class="w-full px-4">
            <div class="border-line hidden border-b font-bold md:grid md:grid-cols-12">
                <div class="col-span-5 px-4 py-2">{{ t('settings.schedule.jobName') }}</div>
                <div class="col-span-2 px-4 py-2">{{ t('settings.schedule.state') }}</div>
                <div class="col-span-2 px-4 py-2">{{ t('settings.schedule.lastExecution') }}</div>
                <div class="col-span-2 px-4 py-2">{{ t('settings.schedule.nextExecution') }}</div>
                <div class="col-span-1 px-4 py-2">{{ t('settings.schedule.actions') }}</div>
            </div>

            <div
                v-for="job in jobs"
                :key="job.id"
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b py-3 md:grid md:grid-cols-12 md:gap-0 md:py-0">
                <div class="flex w-full items-center gap-2 md:col-span-5 md:w-auto md:px-4 md:py-2">
                    <span class="min-w-0 flex-1 md:flex-none">{{ job.id }}</span>
                    <span class="ml-auto flex-none md:hidden">
                        <TriggerJob
                            :title="t('settings.schedule.run')"
                            @toggle:trigger="scheduleStore.startJob(job.id)" />
                    </span>
                </div>
                <div class="flex items-center md:col-span-2 md:px-4 md:py-2">
                    {{ job.currentState }}
                </div>
                <div
                    :class="job.lastExecution ? 'flex' : 'hidden md:flex'"
                    class="items-center gap-2 md:col-span-2 md:px-4 md:py-2">
                    <span v-if="job.lastExecution">
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
                    <span v-if="job.nextExecution">
                        {{ formatDateTime(job.nextExecution) }}
                    </span>
                    <span
                        class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                        {{ t('settings.schedule.nextRun') }}
                    </span>
                </div>
                <div class="hidden md:col-span-1 md:flex md:items-center md:px-4 md:py-2">
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
import { Hub } from '@/types'
import { formatDateTime } from '@/utils/date'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'

const scheduleStore = useScheduleStore()
const signalR = useSignalR()
const { t } = useI18n()
const hubConnection = ref<Hub>()
const jobs = computed(() => scheduleStore.getRecurringJobs)

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
