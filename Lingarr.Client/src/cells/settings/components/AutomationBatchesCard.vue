<template>
    <!-- ═══ 每日补缺策略卡(设置 · 自动化 tab):行 hint 只报策略(限额/新片延迟),
         cron 归任务计划页任务表唯一编辑;编辑模态留三个策略字段,cron 只读展示
         + 下钻链接。「每周全库巡检」服务端不存在,不显示。 -->
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
            <span class="ml-auto flex flex-none items-center gap-2.5">
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

        <!-- 编辑模态:cron 只读(编辑入口在任务计划)+ 单次限额 + 新片延迟,与开关解耦 -->
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
                            <!-- cron 只读行:任务表是 cron 的唯一编辑入口 -->
                            <div
                                class="border-line flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-md border px-3 py-2.5"
                                data-test="batch-modal-cron">
                                <b class="text-[13px] font-semibold">
                                    {{ t('settings.automation.scheduleField') }}
                                </b>
                                <span class="mono text-secondary-content min-w-0 flex-1 truncate text-xs">
                                    {{ translationSchedule || '—' }}
                                </span>
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                                    data-test="batch-open-schedule"
                                    @click="openSchedule">
                                    {{ t('settings.automation.editInSchedule') }}
                                </button>
                            </div>
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
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useScheduleStore } from '@/cells/settings/stores/schedule'
import { useToastStore } from '@/cells/app/stores/toast'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

const router = useRouter()
const settingsStore = useSettingStore()
const scheduleStore = useScheduleStore()
const toastStore = useToastStore()
const { t } = useI18n()

const automationEnabled = computed({
    get: (): string => (settingsStore.getSetting(SETTINGS.AUTOMATION_ENABLED) as string) ?? 'false',
    // The job enable/disable endpoint is the same source of truth as the
    // schedule table toggle: it persists automation_enabled AND registers
    // the Hangfire job immediately — a bare setting write never registered
    // anything until the next restart.
    set: (newValue: string): void => {
        scheduleStore
            .setJobEnabled('AutomatedTranslationJob', newValue === 'true')
            .then(() => {
                settingsStore.storeSetting(SETTINGS.AUTOMATION_ENABLED, newValue)
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

function openSchedule(): void {
    router.push({ name: 'schedule' })
}

// ── 编辑模态:本地草稿,保存时一次性写回(参数与开关解耦) ────────────
const editOpen = ref(false)
const saveBlocked = ref(false)

const draftLimit = ref('')
const draftMovieAge = ref('')
const draftShowAge = ref('')
const draftLimitValid = ref(false)
const draftMovieAgeValid = ref(false)
const draftShowAgeValid = ref(false)

/// 限额正整数 / 延迟非负整数(InputComponent 数字校验已覆盖数值性,
/// 这里再钳必填)
const canSave = computed(
    () =>
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
    draftLimit.value = maxTranslationsPerRun.value
    draftMovieAge.value = movieAgeThreshold.value
    draftShowAge.value = showAgeThreshold.value
    // 回显值来自已持久化设置,先按有效处理;用户改动后由 InputComponent 校验接管
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
    await Promise.all([
        settingsStore.updateSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN, draftLimit.value, true),
        settingsStore.updateSetting(SETTINGS.MOVIE_AGE_THRESHOLD, draftMovieAge.value, true),
        settingsStore.updateSetting(SETTINGS.SHOW_AGE_THRESHOLD, draftShowAge.value, true)
    ])
    toastStore.push({
        message:
            automationEnabled.value === 'true'
                ? t('common.saved')
                : `${t('common.saved')} · ${t('settings.status.disabled')}`,
        type: 'success'
    })
    editOpen.value = false
}
</script>
