<template>
    <!-- ═══ 定时任务卡(自 screen-12 下钻屏收编,IA 重构批 3):全部 recurring
         任务表(cron 唯一编辑入口·启停·触发·上次/下次)+「＋ 新建任务」菜单
         (预设仅运维类 + 自定义 cron 表单)。队列深度/成功率归概览,不进设置。 -->
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
                <!-- 「＋ 新建任务」:预设(运维 job 类型)+ 自定义 cron 表单 -->
                <div ref="menuAnchor" class="relative">
                    <button
                        type="button"
                        class="bg-accent text-accent-content hover:brightness-106 focus-visible:ring-accent cursor-pointer rounded-md border border-accent px-2.5 py-1 text-xs font-semibold transition-colors focus-visible:ring-2 focus-visible:outline-none"
                        data-test="schedule-new-job"
                        @click="newJobMenuOpen = !newJobMenuOpen">
                        {{ t('settings.schedule.newJob') }}
                    </button>
                    <div
                        v-if="newJobMenuOpen"
                        class="border-line bg-secondary absolute right-0 z-30 mt-1.5 w-64 rounded-md border py-1 shadow-lg"
                        role="menu"
                        data-test="schedule-new-job-menu">
                        <button
                            v-for="preset in presets"
                            :key="preset.id"
                            type="button"
                            class="hover:bg-tertiary flex w-full cursor-pointer items-center gap-2 px-3.5 py-1.5 text-left text-xs transition-colors"
                            role="menuitem"
                            @click="createPreset(preset)">
                            <span class="min-w-0 flex-1 truncate">{{ preset.label }}</span>
                            <span class="mono text-secondary-content flex-none text-[11px]">
                                {{ preset.cron }}
                            </span>
                        </button>
                        <div class="border-line my-1 border-t"></div>
                        <button
                            type="button"
                            class="hover:bg-tertiary flex w-full cursor-pointer items-center gap-2 px-3.5 py-1.5 text-left text-xs transition-colors"
                            role="menuitem"
                            data-test="schedule-custom-cron"
                            @click="openCustomForm">
                            {{ t('settings.schedule.customCron') }}
                        </button>
                    </div>
                </div>
            </span>
        </div>

        <div class="overflow-x-auto">
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
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b py-3 transition-opacity md:grid md:grid-cols-12 md:gap-0 md:py-0"
                :class="job.enabled ? 'opacity-100' : 'opacity-55'">
                <div class="flex w-full items-center gap-2 md:col-span-4 md:w-auto md:px-4 md:py-2">
                    <span class="mono min-w-0 flex-1 truncate md:flex-none" :title="job.id">
                        {{ job.id }}
                    </span>
                    <span class="ml-auto flex-none md:hidden">
                        <JobToggle :job="job" @toggle="setEnabled" />
                    </span>
                </div>
                <!-- cron 列:唯一编辑入口(停用行亦可改,启用即生效) -->
                <div
                    class="flex items-center gap-1.5 md:col-span-2 md:px-4 md:py-2"
                    :data-test="`schedule-row-${job.id}`">
                    <span class="mono text-secondary-content truncate">{{ job.cron || '—' }}</span>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-1.5 py-px text-[10.5px] transition-colors"
                        :data-test="`schedule-cron-edit-${job.id}`"
                        @click="openCronEdit(job)">
                        {{ t('settings.automation.edit') }}
                    </button>
                </div>
                <div
                    :class="job.lastExecution ? 'flex' : 'hidden md:flex'"
                    class="items-center gap-2 md:col-span-2 md:px-4 md:py-2">
                    <span v-if="job.lastExecution" class="mono text-secondary-content text-xs">
                        {{ formatDateTime(job.lastExecution) }}
                    </span>
                    <span v-else class="mono text-secondary-content text-xs">—</span>
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
                    <span v-else class="mono text-secondary-content text-xs">—</span>
                    <span
                        class="text-primary-content/50 text-xs font-semibold tracking-wide uppercase md:hidden">
                        {{ t('settings.schedule.nextRun') }}
                    </span>
                </div>
                <div class="flex items-center md:col-span-1 md:px-4 md:py-2">
                    <!-- 状态 chip 联动:关闭(dim)/运行中 busy/上次成功 ok/其他 dim -->
                    <span
                        class="mono rounded-[3px] border px-1.5 py-px text-[11px]"
                        :class="stateChipClass(job)"
                        :title="job.currentState">
                        {{ stateChipLabel(job) }}
                    </span>
                </div>
                <div
                    class="hidden md:col-span-1 md:flex md:items-center md:justify-end md:gap-1 md:px-4 md:py-2">
                    <TriggerJob
                        v-if="job.enabled"
                        :title="t('settings.schedule.run')"
                        @toggle:trigger="scheduleStore.startJob(job.id)" />
                    <JobToggle :job="job" @toggle="setEnabled" />
                </div>
            </div>
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

        <!-- ═══ 自定义 cron 表单(名称 + 运维 job 类型 + cron) ─────────── -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="formOpen"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="formOpen = false"
                    @keydown.escape="formOpen = false">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        data-test="schedule-new-job-form"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">
                                {{ t('settings.schedule.createJobTitle') }}
                            </h2>
                        </div>
                        <div class="space-y-3">
                            <InputComponent
                                v-model="formJobId"
                                :label="t('settings.schedule.jobIdField')"
                                :placeholder="'nightly-scan'"
                                :debounce="0" />
                            <div>
                                <label class="mb-1 block text-sm" for="schedule-job-type">
                                    {{ t('settings.schedule.jobTypeField') }}
                                </label>
                                <select
                                    id="schedule-job-type"
                                    v-model="formJobType"
                                    class="border-line bg-primary text-primary-content focus:border-accent w-full cursor-pointer rounded-md border px-2.5 py-1.5 text-sm outline-none">
                                    <option v-for="preset in presets" :key="preset.id" :value="preset.jobType">
                                        {{ preset.label }}
                                    </option>
                                </select>
                            </div>
                            <InputComponent
                                v-model="formCron"
                                :label="t('settings.schedule.cronField')"
                                :validation-type="INPUT_VALIDATION_TYPE.CRON"
                                :error-message="t('settings.automation.cronFormat')"
                                :placeholder="'0 5 * * *'"
                                :debounce="0"
                                @update:validation="(val) => (formCronValid = val)" />
                            <p v-if="createBlocked" class="text-danger text-xs">
                                {{ createBlockedMessage }}
                            </p>
                            <div class="flex justify-end gap-2 pt-1">
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                    data-test="schedule-form-close"
                                    @click="formOpen = false">
                                    {{ t('common.close') }}
                                </button>
                                <button
                                    type="button"
                                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                    :disabled="!canCreate"
                                    data-test="schedule-create-save"
                                    @click="createCustom">
                                    {{ t('settings.schedule.create') }}
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
import useClickOutside from '@/cells/kernel/composables/useClickOutside'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'
import JobToggle from '@/cells/settings/components/JobToggle.vue'
import InputComponent from '@/cells/settings/components/InputComponent.vue'

/// 预设 = 运维类 job 类型(服务端 CreatableJobTypes 对齐;业务 job 为全库
/// 单例,不可造重复实例)+ 默认 cron;id 为可读 slug,与内置 id 不冲突,
/// 重复创建由 409 拦截。
interface JobPreset {
    id: string
    jobType: string
    cron: string
    label: string
}

const scheduleStore = useScheduleStore()
const signalR = useSignalR()
const toastStore = useToastStore()
const router = useRouter()
const { t } = useI18n()
const hubConnection = ref<Hub>()
const jobs = computed(() => scheduleStore.getRecurringJobs)

const presets = computed<JobPreset[]>(() => [
    {
        id: 'statistics-snapshot',
        jobType: 'StatisticsJob',
        cron: '0 3 * * *',
        label: t('settings.schedule.preset.statistics')
    },
    {
        id: 'weekly-cleanup',
        jobType: 'CleanupJob',
        cron: '0 2 * * 0',
        label: t('settings.schedule.preset.cleanup')
    }
])

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

// ── 新建任务:菜单 + 预设 + 自定义表单 ───────────────────────────
const newJobMenuOpen = ref(false)
const menuAnchor = ref<HTMLElement>()
useClickOutside(menuAnchor, () => {
    newJobMenuOpen.value = false
})

const formOpen = ref(false)
const createBlocked = ref(false)
const formJobId = ref('')
const formJobType = ref('StatisticsJob')
const formCron = ref('0 5 * * *')
const formCronValid = ref(false)

/// Job id validity is checked in-page (no server validator involved):
/// non-empty and free of whitespace. The cron field carries the shared
/// cron validator through InputComponent.
const formJobIdValid = computed(
    () => formJobId.value.trim() !== '' && !/\s/.test(formJobId.value.trim())
)

const canCreate = computed(() => formJobIdValid.value && formCronValid.value)

const createBlockedMessage = computed(() => {
    if (/\s/.test(formJobId.value.trim())) {
        return t('settings.schedule.jobIdWhitespace')
    }
    return t('settings.schedule.jobExists')
})

function closeMenu(): void {
    newJobMenuOpen.value = false
}

async function createPreset(preset: JobPreset): Promise<void> {
    closeMenu()
    try {
        await scheduleStore.createJob({
            jobId: preset.id,
            jobType: preset.jobType,
            cron: preset.cron
        })
        toastStore.push({
            message: t('settings.schedule.createdToast', { job: preset.id, cron: preset.cron }),
            type: 'success'
        })
    } catch (error) {
        toastStore.push({ message: createErrorMessage(error), type: 'error' })
    }
}

function openCustomForm(): void {
    closeMenu()
    formJobId.value = ''
    formJobType.value = 'StatisticsJob'
    formCron.value = '0 5 * * *'
    // 初值来自预设,先按有效处理;用户改动后由 InputComponent 校验接管
    formCronValid.value = true
    createBlocked.value = false
    formOpen.value = true
}

async function createCustom(): Promise<void> {
    if (!canCreate.value) {
        createBlocked.value = true
        return
    }
    const jobId = formJobId.value.trim()
    try {
        await scheduleStore.createJob({ jobId, jobType: formJobType.value, cron: formCron.value })
        toastStore.push({
            message: t('settings.schedule.createdToast', { job: jobId, cron: formCron.value }),
            type: 'success'
        })
        formOpen.value = false
    } catch (error) {
        toastStore.push({ message: createErrorMessage(error), type: 'error' })
    }
}

/// 400 = cron/类型无效,409 = id 已占用;其余归为通用失败。
function createErrorMessage(error: unknown): string {
    const status = axios.isAxiosError(error) ? error.response?.status : undefined
    if (status === 400) {
        return t('settings.schedule.cronInvalid')
    }
    if (status === 409) {
        return t('settings.schedule.jobExists')
    }
    return t('settings.schedule.createFailed')
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
    await refresh()
    hubConnection.value = await signalR.connect('JobProgress', '/signalr/JobProgress')
    await hubConnection.value.joinGroup({ group: 'JobProgress' })

    hubConnection.value.on('JobStateUpdated', onJobStateUpdated)
})

onUnmounted(async () => {
    hubConnection.value?.off('JobStateUpdated', onJobStateUpdated)
})
</script>
