<template>
    <!-- ═══ 定时任务卡(设置 · 自动化 tab):运维任务目录——4 个内置任务
         (±存量 custom 行)。AutomatedTranslationJob 收归上方自动化批次卡
         (开关/触发/cron/策略单点驻留),本表不再渲染,勿再此行加回。
         表 = cron 唯一编辑入口·启停·触发·上次/下次;「＋ 新建任务」已移除
         (PM 裁决:复制单例运维任务无独立价值);队列深度/成功率归概览,不进设置。 -->
    <div
        id="jobs"
        class="border-line bg-secondary scroll-mt-24 rounded-md border"
        data-test="jobs-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.schedule.heading') }}</h2>
            <span class="text-secondary-content mono ml-auto text-[11px]">{{ subLine }}</span>
            <span class="flex flex-none items-center gap-1.5">
                <ReloadComponent @toggle:update="refresh" />
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                    data-test="schedule-view-queue"
                    @click="router.push({ name: 'queue' })">
                    {{ t('settings.schedule.viewQueue') }}
                </button>
            </span>
        </div>

        <!-- TZ 未设置/无效回退 UTC 时,服务端日志之外的唯一可见提示 -->
        <div
            v-if="timeZone?.warning"
            class="text-warning border-line/50 border-b px-4 py-2 text-[12px]"
            data-test="schedule-timezone-warning"
            :title="timeZone.warning">
            ⚠ {{ t('settings.schedule.timeZoneWarning') }}
        </div>

        <div class="shell-scroll overflow-x-auto">
            <table class="w-full border-collapse text-[12.5px]">
                <thead>
                    <tr>
                        <th :class="thClass" class="min-w-[200px]">
                            {{ t('settings.schedule.jobName') }}
                        </th>
                        <th :class="thClass">{{ t('settings.schedule.cron') }}</th>
                        <th :class="thClass">{{ t('settings.schedule.lastExecution') }}</th>
                        <th :class="thClass">
                            <span class="flex items-center gap-1.5">
                                {{ t('settings.schedule.nextExecution') }}
                                <!-- cron 与本列时间同源时区,标注以免按浏览器时区误读 -->
                                <span
                                    v-if="timeZone"
                                    class="text-secondary-content/60 text-[10.5px] font-normal"
                                    data-test="schedule-timezone-id">
                                    {{ timeZone.ianaId }}
                                </span>
                            </span>
                        </th>
                        <th :class="thClass">{{ t('settings.schedule.state') }}</th>
                        <th :class="`${thClass} text-right`">
                            {{ t('settings.schedule.actions') }}
                        </th>
                    </tr>
                </thead>
                <tbody>
                    <tr
                        v-for="job in jobs"
                        :key="job.id"
                        class="hover:bg-tertiary/50 transition-opacity"
                        :class="job.enabled ? 'opacity-100' : 'opacity-55'"
                        :data-test="`schedule-row-${job.id}`">
                        <td class="mono px-3 py-2.5 font-semibold" :title="job.id">
                            {{ job.id }}
                        </td>
                        <!-- cron 列:唯一编辑入口(停用行亦可改,启用即生效) -->
                        <td class="border-line/50 border-b px-3 py-2.5 align-middle">
                            <span class="flex items-center gap-1.5 whitespace-nowrap">
                                <span class="mono text-secondary-content">{{ job.cron || '—' }}</span>
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-1.5 py-px text-[10.5px] transition-colors"
                                    :data-test="`schedule-cron-edit-${job.id}`"
                                    @click="openCronEdit(job)">
                                    {{ t('settings.automation.edit') }}
                                </button>
                            </span>
                        </td>
                        <td class="border-line/50 border-b px-3 py-2.5 align-middle">
                            <span class="mono text-secondary-content text-xs">
                                {{ job.lastExecution ? formatDateTime(job.lastExecution) : '—' }}
                            </span>
                        </td>
                        <td class="border-line/50 border-b px-3 py-2.5 align-middle">
                            <span class="mono text-secondary-content text-xs">
                                {{ job.nextExecution ? formatDateTime(job.nextExecution) : '—' }}
                            </span>
                        </td>
                        <td class="border-line/50 border-b px-3 py-2.5 align-middle">
                            <!-- 状态 chip 联动:关闭(dim)/运行中 busy/上次成功 ok/其他 dim -->
                            <span
                                class="mono rounded-[3px] border px-1.5 py-px text-[11px]"
                                :class="stateChipClass(job)"
                                :title="job.currentState">
                                {{ stateChipLabel(job) }}
                            </span>
                        </td>
                        <td
                            class="border-line/50 border-b px-3 py-2.5 text-right align-middle whitespace-nowrap">
                            <span class="inline-flex items-center justify-end gap-1">
                                <TriggerJob
                                    v-if="job.enabled"
                                    :title="t('settings.schedule.run')"
                                    @toggle:trigger="scheduleStore.startJob(job.id)" />
                                <JobToggle :job="job" @toggle="setEnabled" />
                            </span>
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>

        <!-- ═══ cron 编辑模态:任务表的 cron 是全产品唯一编辑入口 ───────── -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="cronEditId !== null"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="cronEditId = null"
                    @keydown.escape="cronEditId = null">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        data-test="schedule-cron-modal"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="mono text-[15px] font-semibold">{{ cronEditId }}</h2>
                        </div>
                        <div class="space-y-3">
                            <InputComponent
                                v-model="draftCron"
                                :label="t('settings.schedule.cronField')"
                                :validation-type="INPUT_VALIDATION_TYPE.CRON"
                                :error-message="t('settings.automation.cronFormat')"
                                :placeholder="'0 5 * * *'"
                                :debounce="0"
                                @update:validation="(val) => (draftCronValid = val)" />
                            <!-- 写 cron 的现场标注解释时区,与「下次执行」列头同源 -->
                            <p
                                v-if="timeZone"
                                class="text-secondary-content/70 text-[11px]"
                                data-test="schedule-cron-timezone">
                                {{ t('settings.schedule.cronTimeZone', { timeZone: timeZone.ianaId }) }}
                            </p>
                            <div class="flex justify-end gap-2 pt-1">
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                    data-test="schedule-cron-close"
                                    @click="cronEditId = null">
                                    {{ t('common.close') }}
                                </button>
                                <button
                                    type="button"
                                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                    :disabled="!draftCronValid"
                                    data-test="schedule-cron-save"
                                    @click="saveCron">
                                    {{ t('common.save') }}
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>

    </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import { Hub, IRecurringJob, INPUT_VALIDATION_TYPE } from '@/types'
import { formatDateTime } from '@/cells/app/utils/date'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { useToastStore } from '@/cells/app/stores/toast'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'
import JobToggle from '@/cells/settings/components/JobToggle.vue'
import InputComponent from '@/cells/settings/components/InputComponent.vue'

const scheduleStore = useScheduleStore()
const signalR = useSignalR()
const toastStore = useToastStore()
const router = useRouter()
const { t } = useI18n()
const hubConnection = ref<Hub>()
/// AutomatedTranslationJob 归自动化批次卡,本表只列运维任务(4 内置±custom)。
const jobs = computed(() =>
    scheduleStore.getRecurringJobs.filter((job) => job.id !== 'AutomatedTranslationJob')
)
const timeZone = computed(() => scheduleStore.timeZone)

/// 应用表格惯用法(与媒体库矩阵一致,原型 th 规范:12px/字重 500/次级色)
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'

/// 卡头 meta 只用真实数据:任务未加载时仅「Hangfire recurring」,加载后
/// 附任务数与队列数。
const subLine = computed(() => {
    if (jobs.value.length === 0) {
        return t('settings.schedule.subLineBase')
    }
    const queues = new Set(jobs.value.map((job) => job.queue).filter((queue) => queue))
    return t('settings.schedule.subLine', { jobs: jobs.value.length, queues: queues.size })
})

const stateChipClass = (job: IRecurringJob): string => {
    if (!job.enabled) {
        return 'text-secondary-content border-line bg-tertiary opacity-55'
    }
    if (job.isCurrentlyRunning) {
        return 'text-info-content border-info/40 bg-info/20'
    }
    if (job.lastJobState === 'Succeeded') {
        return 'text-success-content border-success/40 bg-success/50'
    }
    return 'text-secondary-content border-line bg-tertiary opacity-55'
}

const stateChipLabel = (job: IRecurringJob): string => {
    if (!job.enabled) {
        return t('settings.schedule.stateDisabled')
    }
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

// ── 启用/停用(与策略卡开关同源) ─────────────────────────────────
async function setEnabled(job: IRecurringJob, enabled: boolean): Promise<void> {
    try {
        await scheduleStore.setJobEnabled(job.id, enabled)
        toastStore.push({
            message: enabled
                ? t('settings.schedule.enabledToast', { job: job.id })
                : t('settings.schedule.disabledToast', { job: job.id }),
            type: 'success'
        })
    } catch {
        toastStore.push({ message: t('settings.schedule.toggleFailed'), type: 'error' })
    }
}

// ── cron 编辑:任务表 = 全产品唯一编辑入口 ────────────────────────
const cronEditId = ref<string | null>(null)
const draftCron = ref('')
const draftCronValid = ref(false)

function openCronEdit(job: IRecurringJob): void {
    cronEditId.value = job.id
    draftCron.value = job.cron
    // 回显值来自已注册/已持久化的表达式,先按有效处理;改动后由校验接管
    draftCronValid.value = true
}

async function saveCron(): Promise<void> {
    if (cronEditId.value === null || !draftCronValid.value) return
    try {
        await scheduleStore.updateJobCron(cronEditId.value, draftCron.value)
        toastStore.push({
            message: t('settings.schedule.cronUpdated', { job: cronEditId.value, cron: draftCron.value }),
            type: 'success'
        })
        cronEditId.value = null
    } catch (error) {
        toastStore.push({ message: cronEditErrorMessage(error), type: 'error' })
    }
}

/// 400 = cron 无效,404 = 未知任务,409 = 设置行缺失(迁移未跑);其余通用失败。
function cronEditErrorMessage(error: unknown): string {
    const status = axios.isAxiosError(error) ? error.response?.status : undefined
    if (status === 400) {
        return t('settings.schedule.cronInvalid')
    }
    if (status === 404) {
        return t('settings.schedule.jobNotFound')
    }
    return t('settings.schedule.cronUpdateFailed')
}

async function refresh(): Promise<void> {
    await scheduleStore.fetchRecurringJobs()
}

const onJobStateUpdated = (jobId: string, state: string) => {
    const job = jobs.value.find((job) => job.id === jobId)
    if (job) {
        job.currentState = state
    }
}

onMounted(async () => {
    await Promise.all([refresh(), scheduleStore.fetchTimeZone()])
    hubConnection.value = await signalR.connect('JobProgress', '/signalr/JobProgress')
    await hubConnection.value.joinGroup({ group: 'JobProgress' })

    hubConnection.value.on('JobStateUpdated', onJobStateUpdated)
})

onUnmounted(async () => {
    hubConnection.value?.off('JobStateUpdated', onJobStateUpdated)
})
</script>
