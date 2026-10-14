<template>
    <!-- ═══ 定时任务下钻屏(原型 screen-12 完整形态):pb-head(← 系统 +
         Hangfire 副行 + 查看任务队列 + 「＋ 新建任务」CTA 菜单)→ 队列深度
         六卡(qdepth)→ 任务表(启停 chip 联动 + 触发)。深度/启停/新建走
         /api/schedule 的 depth、job/{id}/enable|disable、job 端点。 -->
    <DrillDownPage
        :back-to="{ name: 'settings-system' }"
        :back-label="t('settings.menu.groups.system')"
        :title="t('settings.schedule.heading')"
        :sub="subLine">
        <template #actions>
            <ReloadComponent @toggle:update="refresh" />
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="schedule-view-queue"
                @click="router.push({ name: 'queue' })">
                {{ t('settings.schedule.viewQueue') }}
            </button>
            <!-- 「＋ 新建任务」:预设(现有 job 类型)+ 自定义 cron 表单 -->
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
        </template>

        <!-- ═══ 队列深度六卡(原型 .qdepth):translation 排队(accent)/
             正在执行 / system 排队 / movies+shows / webhook+default /
             30 天成功率(既有统计)。 -->
        <div class="grid shrink-0 grid-cols-2 gap-2.5 md:grid-cols-3" data-test="schedule-qdepth">
            <div
                v-for="card in depthCards"
                :key="card.label"
                class="border-line bg-secondary rounded-md border px-4 py-2.5">
                <div
                    class="mono text-[20px] leading-tight font-semibold"
                    :class="card.accent ? 'text-accent' : ''">
                    {{ card.value }}
                </div>
                <div class="text-secondary-content mt-0.5 text-[11.5px]">{{ card.label }}</div>
            </div>
        </div>

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
                <div class="mono text-secondary-content md:col-span-2 md:px-4 md:py-2">
                    {{ job.cron || '—' }}
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

        <!-- ═══ 自定义 cron 表单(原型 openForm):名称 + job 类型 + cron -->
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
    </DrillDownPage>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import { Hub, IRecurringJob, INPUT_VALIDATION_TYPE } from '@/types'
import { formatDateTime } from '@/cells/app/utils/date'
import { useSignalR } from '@/cells/app/composables/useSignalR'
import { useUiStore } from '@/cells/app/stores/ui'
import { useToastStore } from '@/cells/app/stores/toast'
import { statisticsService } from '@/cells/app/api/statisticsService'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import useClickOutside from '@/cells/kernel/composables/useClickOutside'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'
import JobToggle from '@/cells/settings/components/JobToggle.vue'
import InputComponent from '@/cells/settings/components/InputComponent.vue'

/// 预设 = 现有 job 类型(服务端 RegisteredJobTypes)+ 默认 cron;
/// id 为可读 slug,与内置 id 不冲突,重复创建由 409 拦截。
interface JobPreset {
    id: string
    jobType: string
    cron: string
    label: string
}

const scheduleStore = useScheduleStore()
const signalR = useSignalR()
const uiStore = useUiStore()
const toastStore = useToastStore()
const router = useRouter()
const { t } = useI18n()
const statisticsApi = statisticsService(axios)
const hubConnection = ref<Hub>()
const jobs = computed(() => scheduleStore.getRecurringJobs)
const depths = computed(() => scheduleStore.getQueueDepths)
const successRate = ref<number | null>(null)

const presets = computed<JobPreset[]>(() => [
    {
        id: 'daily-gap-fill',
        jobType: 'AutomatedTranslationJob',
        cron: '0 4 * * *',
        label: t('settings.schedule.preset.automatedTranslation')
    },
    {
        id: 'statistics-snapshot',
        jobType: 'StatisticsJob',
        cron: '0 3 * * *',
        label: t('settings.schedule.preset.statistics')
    },
    {
        id: 'media-sync-shows',
        jobType: 'SyncShowJob',
        cron: '*/30 * * * *',
        label: t('settings.schedule.preset.syncShow')
    },
    {
        id: 'media-sync-movies',
        jobType: 'SyncMovieJob',
        cron: '0 */2 * * *',
        label: t('settings.schedule.preset.syncMovie')
    },
    {
        id: 'weekly-cleanup',
        jobType: 'CleanupJob',
        cron: '0 2 * * 0',
        label: t('settings.schedule.preset.cleanup')
    }
])

/// mono 副行只用真实数据:任务未加载时仅「Hangfire recurring」,加载后
/// 附任务数与队列数(并发上限无端点/设置可读,不展示)。
const subLine = computed(() => {
    if (jobs.value.length === 0) {
        return t('settings.schedule.subLineBase')
    }
    const queues = new Set(jobs.value.map((job) => job.queue).filter((queue) => queue))
    return t('settings.schedule.subLine', { jobs: jobs.value.length, queues: queues.size })
})

/// 深度卡聚合:translation 单列(accent),执行中为全部队列 processing 之和,
/// movies+shows、webhook+default 合并卡;成功率卡用既有统计端点。
const depthValue = (queue: string): number =>
    depths.value.find((depth) => depth.queue === queue)?.enqueued ?? 0

const depthCards = computed(() => [
    {
        label: t('settings.schedule.depth.translationQueued'),
        value: depthValue('translation'),
        accent: true
    },
    {
        label: t('settings.schedule.depth.executing'),
        value: depths.value.reduce((sum, depth) => sum + depth.processing, 0),
        accent: false
    },
    {
        label: t('settings.schedule.depth.systemQueued'),
        value: depthValue('system'),
        accent: false
    },
    {
        label: t('settings.schedule.depth.mediaQueued'),
        value: depthValue('movies') + depthValue('shows'),
        accent: false
    },
    {
        label: t('settings.schedule.depth.webhookQueued'),
        value: depthValue('webhook') + depthValue('default'),
        accent: false
    },
    {
        label: t('settings.schedule.depth.successRate'),
        value:
            successRate.value != null
                ? `${Intl.NumberFormat('en-US', { maximumFractionDigits: 1 }).format(successRate.value)}%`
                : '—',
        accent: false
    }
])

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

// ── 启用/停用(与设置·自动化批次开关同源) ───────────────────────
async function setEnabled(job: IRecurringJob, enabled: boolean): Promise<void> {
    try {
        await scheduleStore.setJobEnabled(job.id, enabled)
        toastStore.push({
            message: enabled
                ? t('settings.schedule.enabledToast', { job: job.id })
                : t('settings.schedule.disabledToast', { job: job.id }),
            type: 'success'
        })
        void scheduleStore.fetchQueueDepths()
    } catch {
        toastStore.push({ message: t('settings.schedule.toggleFailed'), type: 'error' })
    }
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
    await Promise.allSettled([
        scheduleStore.fetchRecurringJobs(),
        scheduleStore.fetchQueueDepths(),
        loadSuccessRate()
    ])
}

async function loadSuccessRate(): Promise<void> {
    try {
        const pipeline = await statisticsApi.getPipelineStatistics()
        successRate.value = pipeline.successRate30Days
    } catch {
        successRate.value = null
    }
}

const onJobStateUpdated = (jobId: string, state: string) => {
    const job = jobs.value.find((job) => job.id === jobId)
    if (job) {
        job.currentState = state
    }
}

// 顶栏 CTA 与页内按钮共用同一菜单(global-interactions:CTA 动作集中)
onMounted(() => {
    uiStore.registerCta(t('settings.schedule.newJob'), () => {
        newJobMenuOpen.value = true
    })
})
onUnmounted(() => {
    uiStore.clearCta()
})

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
