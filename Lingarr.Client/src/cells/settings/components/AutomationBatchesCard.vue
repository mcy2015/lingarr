<template>
    <!-- ═══ 每日补缺卡(设置 · 自动化 tab):AutomatedTranslationJob 的唯一前端
         驻留位——开关/下次执行/手动触发/cron 编辑/策略参数全在此,任务表不再
         渲染该行(单开关单源,重复展示的同步问题随之消失)。行 hint 只报策略
         (限额/新片延迟);「每周全库巡检」服务端不存在,不显示。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="batches-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.automation.batchesTitle') }}</h2>
            <span class="text-secondary-content mono ml-auto text-[11px]">
                {{ t('settings.automation.batchesMeta') }}
            </span>
        </div>

        <div
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="batch-row-daily">
            <b class="flex-none">{{ t('settings.automation.dailyRow') }}</b>
            <span class="text-secondary-content mono min-w-0 flex-1 truncate text-xs" :title="dailyHint">
                {{ dailyHint }}
            </span>
            <!-- 下次执行:与 cron 解释同源时区,编辑 cron 的现场即可核对生效时间 -->
            <span
                v-if="automationJob"
                class="text-secondary-content mono flex-none text-xs"
                :title="t('settings.schedule.nextExecution')"
                data-test="batch-next-execution">
                {{ automationJob.nextExecution ? formatDateTime(automationJob.nextExecution) : '—' }}
                <span v-if="timeZone" class="text-secondary-content/60 text-[10.5px]">
                    {{ timeZone.ianaId }}
                </span>
            </span>
            <span class="ml-auto flex flex-none items-center gap-2.5">
                <TriggerJob
                    v-if="automationEnabled === 'true'"
                    @toggle:trigger="scheduleStore.startJob('AutomatedTranslationJob')" />
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    data-test="batch-edit"
                    @click="openEdit">
                    {{ t('settings.automation.edit') }}
                </button>
                <ToggleButton v-model="automationEnabled" size="small">
                    <span class="text-primary-content text-xs font-medium">
                        {{
                            automationEnabled === 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>
            </span>
        </div>

        <!-- F2b:自动化补缺是否消费内嵌文本轨(手动翻译入口不受此开关限制) -->
        <div
            class="border-line/60 flex flex-wrap items-center gap-x-3 gap-y-2 border-t px-4 py-2.5 text-[13px]"
            data-test="batch-row-embedded">
            <b class="flex-none">{{ t('settings.automation.consumeEmbeddedTracks') }}</b>
            <span
                class="text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="t('settings.automation.consumeEmbeddedTracksHint')">
                {{ t('settings.automation.consumeEmbeddedTracksHint') }}
            </span>
            <span class="ml-auto flex flex-none items-center gap-2.5">
                <ToggleButton v-model="consumeEmbeddedTracks" size="small">
                    <span class="text-primary-content text-xs font-medium">
                        {{
                            consumeEmbeddedTracks === 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>
            </span>
        </div>

        <!-- 编辑模态:cron(本卡即编辑入口)+ 三策略字段,与开关解耦 ───────── -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="editOpen"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="editOpen = false"
                    @keydown.escape="editOpen = false">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        data-test="batch-modal"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">{{ t('settings.automation.dailyRow') }}</h2>
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
                            <!-- cron 与执行时间均按此时区解释/显示 -->
                            <p
                                v-if="timeZone"
                                class="text-secondary-content/70 text-[11px]"
                                data-test="batch-cron-timezone">
                                {{ t('settings.schedule.cronTimeZone', { timeZone: timeZone.ianaId }) }}
                            </p>
                            <InputComponent
                                v-model="draftLimit"
                                :type="INPUT_TYPE.NUMBER"
                                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                                :min-length="0"
                                :label="t('settings.automation.limitPerSchedule')"
                                @update:validation="(val) => (draftLimitValid = val)" />
                            <InputComponent
                                v-model="draftMovieAge"
                                :type="INPUT_TYPE.NUMBER"
                                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                                :min-length="0"
                                :label="t('settings.automation.movieAgeDelay')"
                                @update:validation="(val) => (draftMovieAgeValid = val)" />
                            <InputComponent
                                v-model="draftShowAge"
                                :type="INPUT_TYPE.NUMBER"
                                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                                :min-length="0"
                                :label="t('settings.automation.showAgeDelay')"
                                @update:validation="(val) => (draftShowAgeValid = val)" />

                            <p v-if="saveBlocked" class="text-danger text-xs">
                                {{ t('settings.automation.editFailed') }}
                            </p>

                            <div class="flex justify-end gap-2 pt-1">
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                    data-test="batch-modal-close"
                                    @click="editOpen = false">
                                    {{ t('common.close') }}
                                </button>
                                <button
                                    type="button"
                                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                    :disabled="!canSave"
                                    data-test="batch-save"
                                    @click="save">
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
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { formatDateTime } from '@/cells/app/utils/date'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import { useToastStore } from '@/cells/app/stores/toast'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import TriggerJob from '@/cells/settings/components/TriggerJob.vue'

const settingsStore = useSettingStore()
const scheduleStore = useScheduleStore()
const toastStore = useToastStore()
const { t } = useI18n()

const timeZone = computed(() => scheduleStore.timeZone)

/// AutomatedTranslationJob 的唯一驻留位(任务表已过滤该行)。
const automationJob = computed(() =>
    scheduleStore.getRecurringJobs.find((job) => job.id === 'AutomatedTranslationJob')
)

const automationEnabled = computed({
    get: (): string =>
        automationJob.value
            ? String(automationJob.value.enabled)
            : // 任务列表异步水合前的首屏桥接值
              ((settingsStore.getSetting(SETTINGS.AUTOMATION_ENABLED) as string) ?? 'false'),
    // 走任务启停端点:落库 automation_enabled 并即时注册/注销 Hangfire,
    // 裸写设置直到下次重启都不会生效。
    set: (newValue: string): void => {
        scheduleStore
            .setJobEnabled('AutomatedTranslationJob', newValue === 'true')
            .then(() => {
                toastStore.push({ message: t('common.saved'), type: 'success' })
            })
            .catch(() => {
                toastStore.push({
                    message: t('settings.automation.toggleFailed'),
                    type: 'error'
                })
            })
    }
})

const translationSchedule = computed(
    (): string => (settingsStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE) as string) ?? ''
)

/// F2b opt-in: automated runs extract text-based embedded tracks as
/// translation sources. Manual translation from the media page always works.
const consumeEmbeddedTracks = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.CONSUME_EMBEDDED_TRACKS) as string) ?? 'false',
    set: (newValue: string): void => {
        settingsStore
            .updateSetting(SETTINGS.CONSUME_EMBEDDED_TRACKS, newValue, true)
            .then(() => {
                toastStore.push({ message: t('common.saved'), type: 'success' })
            })
            .catch(() => {
                toastStore.push({
                    message: t('settings.automation.toggleFailed'),
                    type: 'error'
                })
            })
    }
})
const maxTranslationsPerRun = computed(
    (): string => (settingsStore.getSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN) as string) ?? ''
)
const movieAgeThreshold = computed(
    (): string => (settingsStore.getSetting(SETTINGS.MOVIE_AGE_THRESHOLD) as string) ?? ''
)
const showAgeThreshold = computed(
    (): string => (settingsStore.getSetting(SETTINGS.SHOW_AGE_THRESHOLD) as string) ?? ''
)

const dailyHint = computed(() =>
    t('settings.automation.dailyHint', {
        limit: maxTranslationsPerRun.value || '—',
        movie: movieAgeThreshold.value || '0',
        show: showAgeThreshold.value || '0'
    })
)

// 本卡自带取数,不依赖同页任务表先挂载(store 内已做并发去重/幂等)
onMounted(() => {
    scheduleStore.fetchRecurringJobs()
    scheduleStore.fetchTimeZone()
})

// ── 编辑模态:本地草稿,保存时一次性写回(参数与开关解耦) ───────────
const editOpen = ref(false)
const saveBlocked = ref(false)

const draftCron = ref('')
const draftLimit = ref('')
const draftMovieAge = ref('')
const draftShowAge = ref('')
const draftCronValid = ref(false)
const draftLimitValid = ref(false)
const draftMovieAgeValid = ref(false)
const draftShowAgeValid = ref(false)

/// cron 走表达式校验;限额正整数 / 延迟非负整数(InputComponent 数字校验
/// 已覆盖数值性,这里再钳必填)
const canSave = computed(
    () =>
        draftCronValid.value &&
        draftLimitValid.value &&
        draftMovieAgeValid.value &&
        draftShowAgeValid.value &&
        Number.isInteger(Number(draftLimit.value)) &&
        Number(draftLimit.value) > 0 &&
        Number.isInteger(Number(draftMovieAge.value)) &&
        Number(draftMovieAge.value) >= 0 &&
        Number.isInteger(Number(draftShowAge.value)) &&
        Number(draftShowAge.value) >= 0
)

function openEdit(): void {
    draftCron.value = automationJob.value?.cron || translationSchedule.value
    draftLimit.value = maxTranslationsPerRun.value
    draftMovieAge.value = movieAgeThreshold.value
    draftShowAge.value = showAgeThreshold.value
    // 回显值来自已注册/已持久化的表达式,先按有效处理;改动后由校验接管
    draftCronValid.value = true
    draftLimitValid.value = true
    draftMovieAgeValid.value = true
    draftShowAgeValid.value = true
    saveBlocked.value = false
    editOpen.value = true
}

async function save(): Promise<void> {
    if (!canSave.value) {
        saveBlocked.value = true
        return
    }
    try {
        const writes: Promise<unknown>[] = [
            settingsStore.updateSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN, draftLimit.value, true),
            settingsStore.updateSetting(SETTINGS.MOVIE_AGE_THRESHOLD, draftMovieAge.value, true),
            settingsStore.updateSetting(SETTINGS.SHOW_AGE_THRESHOLD, draftShowAge.value, true)
        ]
        // cron 未变不重写,避免无谓的重注册
        if (draftCron.value !== (automationJob.value?.cron ?? translationSchedule.value)) {
            writes.push(scheduleStore.updateJobCron('AutomatedTranslationJob', draftCron.value))
        }
        await Promise.all(writes)
        toastStore.push({
            message:
                automationEnabled.value === 'true'
                    ? t('common.saved')
                    : `${t('common.saved')} · ${t('settings.status.disabled')}`,
            type: 'success'
        })
        editOpen.value = false
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
</script>
