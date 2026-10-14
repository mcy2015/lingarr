<template>
    <!-- ═══ 自动化批次卡(原型 screen-06 B 组 setlist 行式):每日补缺行
         (cron + 限额 + 新片延迟的「编辑」模态,参数与开关解耦)+ switch,
         以及媒体源同步(电影/剧集轮询 cron)行组(自 MediaSourcesCard 迁入)。
         「每周全库巡检」服务端不存在,不显示。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="batches-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.automation.batchesTitle') }}</h2>
            <span class="text-secondary-content mono ml-auto text-[11px]">
                {{ t('settings.automation.batchesMeta') }}
            </span>
        </div>

        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
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

        <!-- 媒体源同步(轮询 cron,自 MediaSourcesCard 行组迁入,与每日补缺同组):
             行式(名称 + cron mono + 编辑模态),设置键与校验不变 -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
            data-test="batch-row-movie-index">
            <div class="min-w-0 flex-1">
                <b class="font-semibold">{{ t('settings.sources.movieIndexer') }}</b>
                <div class="text-secondary-content mt-0.5 text-xs leading-snug">
                    {{ t('settings.automation.indexingDescription') }}
                </div>
            </div>
            <span
                class="mono text-secondary-content min-w-0 flex-none truncate text-xs"
                :title="movieSchedule || '—'">
                {{ movieSchedule || '—' }}
            </span>
            <span class="ml-auto flex flex-none items-center">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    data-test="batch-edit-movie-index"
                    @click="openIndexEdit('movie')">
                    {{ t('settings.automation.edit') }}
                </button>
            </span>
        </div>
        <div
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="batch-row-show-index">
            <b class="flex-none">{{ t('settings.sources.showIndexer') }}</b>
            <span
                class="mono text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="showSchedule || '—'">
                {{ showSchedule || '—' }}
            </span>
            <span class="ml-auto flex flex-none items-center">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    data-test="batch-edit-show-index"
                    @click="openIndexEdit('show')">
                    {{ t('settings.automation.edit') }}
                </button>
            </span>
        </div>

        <!-- 编辑模态:cron + 单次限额 + 新片延迟,与开关解耦 -->
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
                                v-model="draftSchedule"
                                :label="t('settings.automation.scheduleField')"
                                :validation-type="INPUT_VALIDATION_TYPE.CRON"
                                :error-message="t('settings.automation.cronFormat')"
                                :placeholder="'0 4 * * *'"
                                @update:validation="(val) => (draftScheduleValid = val)" />
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

        <!-- 索引 cron 编辑模态:cron 必填校验(与原行内输入同规则),保存写回同一设置键 -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="indexEdit !== null"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="indexEdit = null"
                    @keydown.escape="indexEdit = null">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        data-test="index-modal"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">{{ indexEditTitle }}</h2>
                        </div>

                        <div class="space-y-3">
                            <InputComponent
                                v-model="draftIndexCron"
                                :label="t('settings.automation.scheduleField')"
                                :validation-type="INPUT_VALIDATION_TYPE.CRON"
                                :error-message="t('settings.automation.cronFormat')"
                                :placeholder="'0 * * * *'"
                                :debounce="0"
                                @update:validation="(val) => (draftIndexCronValid = val)" />

                            <div class="flex justify-end gap-2 pt-1">
                                <button
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                    data-test="index-modal-close"
                                    @click="indexEdit = null">
                                    {{ t('common.close') }}
                                </button>
                                <button
                                    type="button"
                                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                    :disabled="!draftIndexCronValid"
                                    data-test="index-save"
                                    @click="saveIndex">
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
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

const settingsStore = useSettingStore()
const toastStore = useToastStore()
const { t } = useI18n()

const automationEnabled = computed({
    get: (): string => (settingsStore.getSetting(SETTINGS.AUTOMATION_ENABLED) as string) ?? 'false',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.AUTOMATION_ENABLED, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
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
        cron: translationSchedule.value || '—',
        limit: maxTranslationsPerRun.value || '—',
        movie: movieAgeThreshold.value || '0',
        show: showAgeThreshold.value || '0'
    })
)

// ── 媒体源同步 cron(原 MediaSourcesCard 行组迁入:同一设置键/校验) ──
const movieSchedule = computed(
    (): string => (settingsStore.getSetting(SETTINGS.MOVIE_SCHEDULE) as string) ?? ''
)
const showSchedule = computed(
    (): string => (settingsStore.getSetting(SETTINGS.SHOW_SCHEDULE) as string) ?? ''
)

const indexEdit = ref<'movie' | 'show' | null>(null)
const draftIndexCron = ref('')
const draftIndexCronValid = ref(false)

const indexEditTitle = computed(() =>
    indexEdit.value === 'movie'
        ? t('settings.sources.movieIndexer')
        : t('settings.sources.showIndexer')
)

function openIndexEdit(kind: 'movie' | 'show'): void {
    draftIndexCron.value = kind === 'movie' ? movieSchedule.value : showSchedule.value
    // 回显值来自已持久化设置,先按有效处理;用户改动后由 InputComponent 校验接管
    draftIndexCronValid.value = true
    indexEdit.value = kind
}

async function saveIndex(): Promise<void> {
    if (!draftIndexCronValid.value || indexEdit.value === null) return
    const key = indexEdit.value === 'movie' ? SETTINGS.MOVIE_SCHEDULE : SETTINGS.SHOW_SCHEDULE
    await settingsStore.updateSetting(key, draftIndexCron.value, true)
    toastStore.push({ message: t('common.saved'), type: 'success' })
    indexEdit.value = null
}

// ── 编辑模态:本地草稿,保存时一次性写回(参数与开关解耦) ────────────
const editOpen = ref(false)
const saveBlocked = ref(false)

const draftSchedule = ref('')
const draftLimit = ref('')
const draftMovieAge = ref('')
const draftShowAge = ref('')
const draftScheduleValid = ref(false)
const draftLimitValid = ref(false)
const draftMovieAgeValid = ref(false)
const draftShowAgeValid = ref(false)

/// cron 必填 / 限额正整数 / 延迟非负整数(InputComponent 数字校验已覆盖
/// 数值性,这里再钳必填)
const canSave = computed(
    () =>
        draftScheduleValid.value &&
        draftLimitValid.value &&
        draftMovieAgeValid.value &&
        draftShowAgeValid.value &&
        draftSchedule.value.trim() !== '' &&
        Number.isInteger(Number(draftLimit.value)) &&
        Number(draftLimit.value) > 0 &&
        Number.isInteger(Number(draftMovieAge.value)) &&
        Number(draftMovieAge.value) >= 0 &&
        Number.isInteger(Number(draftShowAge.value)) &&
        Number(draftShowAge.value) >= 0
)

function openEdit(): void {
    draftSchedule.value = translationSchedule.value
    draftLimit.value = maxTranslationsPerRun.value
    draftMovieAge.value = movieAgeThreshold.value
    draftShowAge.value = showAgeThreshold.value
    // 回显值来自已持久化设置,先按有效处理;用户改动后由 InputComponent 校验接管
    draftScheduleValid.value = true
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
        settingsStore.updateSetting(SETTINGS.TRANSLATION_SCHEDULE, draftSchedule.value, true),
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
