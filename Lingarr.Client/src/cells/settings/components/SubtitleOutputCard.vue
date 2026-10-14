<template>
    <!-- ═══ 字幕输出卡(原型 screen-06 C 组 setlist 行式):原 SubtitleSettings
         8 开关 + ValidationSettings 全量转为行式,一个不砍;命名模板行编辑
         真设置 subtitle_naming_template(服务端 CreateFilePath 展开)。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="subtitle-output-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.subtitleOutput.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.subtitle.description') }}
            </span>
        </div>

        <!-- 开关行 -->
        <div
            v-for="row in toggleRows"
            :key="row.key"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 last:border-b-0"
            :data-test="`subtitle-row-${row.key}`">
            <div class="min-w-0 flex-1">
                <b class="text-[13px] font-semibold">{{ row.title }}</b>
                <div class="text-secondary-content mt-0.5 text-[11.5px] leading-snug">
                    {{ row.description }}
                </div>
            </div>
            <ToggleButton v-model="row.model.value" size="small">
                <span class="text-primary-content text-xs font-medium">
                    {{ row.model.value == 'true' ? t('settings.status.enabled') : t('settings.status.disabled') }}
                </span>
            </ToggleButton>
        </div>

        <!-- 字幕标记值(useSubtitleTagging 开时) -->
        <div
            v-if="useSubtitleTagging == 'true'"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5"
            data-test="subtitle-row-tagValue">
            <b class="min-w-0 flex-1 text-[13px] font-semibold">{{ t('settings.subtitle.tagLabel') }}</b>
            <InputComponent
                v-model="subtitleTag"
                class="w-40"
                :validation-type="INPUT_VALIDATION_TYPE.STRING" />
        </div>

        <!-- 校验数值行(校验开后) -->
        <div
            v-for="row in validationRows"
            :key="row.key"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 last:border-b-0"
            :data-test="`subtitle-row-${row.key}`">
            <div class="min-w-0 flex-1">
                <b class="text-[13px] font-semibold">{{ row.title }}</b>
                <div v-if="row.presets" class="mt-1.5 flex flex-wrap gap-1.5">
                    <button
                        v-for="preset in row.presets"
                        :key="preset.label"
                        type="button"
                        class="border-line hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-[11px] transition-colors"
                        @click="row.model.value = preset.value">
                        {{ preset.label }}
                    </button>
                </div>
            </div>
            <InputComponent
                v-model="row.model.value"
                class="w-36"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER" />
        </div>

        <!-- 命名模板行:真实设置 subtitle_naming_template,编辑模态改写 -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="subtitle-row-naming">
            <b class="flex-none">{{ t('settings.subtitleOutput.naming') }}</b>
            <span class="text-secondary-content mono min-w-0 flex-1 truncate text-xs" :title="namingPreview">
                {{ namingPreview }}
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="naming-edit"
                @click="openNamingEdit">
                {{ t('settings.subtitleOutput.edit') }}
            </button>
        </div>

        <!-- 命名模板编辑模态:模板输入(mono)+ 变量说明 + 非空校验 -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="namingModalOpen"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="namingModalOpen = false"
                    @keydown.escape="namingModalOpen = false">
                    <div
                        class="bg-secondary border-line w-full max-w-md rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        :aria-label="t('settings.subtitleOutput.naming')"
                        data-test="naming-modal"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">
                                {{ t('settings.subtitleOutput.naming') }}
                            </h2>
                        </div>

                        <label class="text-secondary-content mb-1.5 block text-xs" for="naming-template-input">
                            {{ t('settings.subtitleOutput.namingTemplateLabel') }}
                        </label>
                        <input
                            id="naming-template-input"
                            v-model="namingDraft"
                            type="text"
                            class="border-line bg-primary text-primary-content mono w-full rounded-md border px-2.5 py-1.5 text-[13px] outline-none"
                            :placeholder="DEFAULT_NAMING_TEMPLATE"
                            data-test="naming-input" />
                        <p class="text-secondary-content mt-2 text-[11.5px] leading-relaxed">
                            {{ t('settings.subtitleOutput.namingVars') }}
                        </p>
                        <p v-if="!namingDraftValid" class="text-danger mt-2 text-xs" data-test="naming-error">
                            {{ t('settings.subtitleOutput.namingRequired') }}
                        </p>

                        <div class="mt-4 flex justify-end gap-2">
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                data-test="naming-cancel"
                                @click="namingModalOpen = false">
                                {{ t('common.close') }}
                            </button>
                            <button
                                type="button"
                                class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                :disabled="!namingDraftValid"
                                data-test="naming-save"
                                @click="saveNaming">
                                {{ t('common.save') }}
                            </button>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>
    </div>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

const settingsStore = useSettingStore()
const toastStore = useToastStore()
const { t } = useI18n()

const isValid = reactive({
    subtitleTag: true,
    maxDurationSecs: true,
    minDurationMs: true,
    minSubtitleLength: true,
    maxSubtitleLength: true,
    maxFileSizeBytes: true
})

/// 与原 SubtitleSettings/ValidationSettings 完全一致的 store 绑定
type SettingKey = (typeof SETTINGS)[keyof typeof SETTINGS]

function bound(setting: SettingKey, validKey?: keyof typeof isValid) {
    return computed({
        get: (): string => (settingsStore.getSetting(setting) as string) ?? '',
        set: (newValue: string): void => {
            const valid = validKey ? isValid[validKey] : true
            settingsStore.updateSetting(setting, newValue, valid)
            if (valid) {
                toastStore.push({ message: t('common.saved'), type: 'success' })
            }
        }
    })
}

const ignoreCaptions = bound(SETTINGS.IGNORE_CAPTIONS)
const fixOverlappingSubtitles = bound(SETTINGS.FIX_OVERLAPPING_SUBTITLES)
const stripSubtitleFormatting = bound(SETTINGS.STRIP_SUBTITLE_FORMATTING)
const preserveLineBreaks = bound(SETTINGS.PRESERVE_LINE_BREAKS)
const bilingualOutput = bound(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT)
const addTranslatorInfo = bound(SETTINGS.ADD_TRANSLATOR_INFO)
const removeLanguageTag = bound(SETTINGS.REMOVE_LANGUAGE_TAG)
const useSubtitleTagging = computed(() => (settingsStore.getSetting(SETTINGS.USE_SUBTITLE_TAGGING) as string) ?? 'false')
const useSubtitleTaggingModel = bound(SETTINGS.USE_SUBTITLE_TAGGING)
const subtitleTag = bound(SETTINGS.SUBTITLE_TAG, 'subtitleTag')

// ── 命名模板(真实设置 subtitle_naming_template)──────────────────
/// 必须与服务端 SettingKeys.Translation.DefaultSubtitleNamingTemplate 一致
const DEFAULT_NAMING_TEMPLATE = '{原名}.{语言}'

const namingTemplate = computed(
    () =>
        (settingsStore.getSetting(SETTINGS.SUBTITLE_NAMING_TEMPLATE) as string | undefined)?.trim() ||
        DEFAULT_NAMING_TEMPLATE
)

/// 行文案 = 模板渲染预览 + 输出示例扩展名 + 写回位置说明
const namingPreview = computed(
    () => `${namingTemplate.value}.srt · ${t('settings.subtitleOutput.namingSuffix')}`
)

const namingModalOpen = ref(false)
const namingDraft = ref('')

const namingDraftValid = computed(() => namingDraft.value.trim().length > 0)

function openNamingEdit(): void {
    namingDraft.value = namingTemplate.value
    namingModalOpen.value = true
}

async function saveNaming(): Promise<void> {
    if (!namingDraftValid.value) return
    await settingsStore.updateSetting(SETTINGS.SUBTITLE_NAMING_TEMPLATE, namingDraft.value.trim(), true)
    namingModalOpen.value = false
    toastStore.push({ message: t('common.saved'), type: 'success' })
}

const validationEnabled = bound(SETTINGS.SUBTITLE_VALIDATION_ENABLED)
const minDurationMs = bound(SETTINGS.SUBTITLE_VALIDATION_MINDURATIONMS, 'minDurationMs')
const maxDurationSecs = bound(SETTINGS.SUBTITLE_VALIDATION_MAXDURATIONSECS, 'maxDurationSecs')
const minSubtitleLength = bound(SETTINGS.SUBTITLE_VALIDATION_MINSUBTITLELENGTH, 'minSubtitleLength')
const maxSubtitleLength = bound(SETTINGS.SUBTITLE_VALIDATION_MAXSUBTITLELENGTH, 'maxSubtitleLength')
const maxFileSizeBytes = bound(SETTINGS.SUBTITLE_VALIDATION_MAXFILESIZEBYTES, 'maxFileSizeBytes')

interface IToggleRow {
    key: string
    title: string
    description: string
    model: { value: string }
}

/// 行序 = 原卡片字段序:8 个开关(含 useSubtitleTagging)→ 校验开关
const toggleRows = computed<IToggleRow[]>(() => [
    {
        key: 'ignoreCaptions',
        title: t('settings.subtitle.ignoreCaptions'),
        description: t('settings.subtitle.ignoreCaptionsDescription'),
        model: ignoreCaptions
    },
    {
        key: 'fixOverlapping',
        title: t('settings.subtitle.fixOverlapping'),
        description: t('settings.subtitle.fixOverlappingDescription'),
        model: fixOverlappingSubtitles
    },
    {
        key: 'stripFormatting',
        title: t('settings.subtitle.stripFormatting'),
        description: t('settings.subtitle.stripFormattingDescription'),
        model: stripSubtitleFormatting
    },
    {
        key: 'preserveLineBreaks',
        title: t('settings.subtitle.preserveLineBreaks'),
        description: t('settings.subtitle.preserveLineBreaksDescription'),
        model: preserveLineBreaks
    },
    {
        key: 'bilingualOutput',
        title: t('settings.subtitle.bilingualOutput'),
        description: t('settings.subtitle.bilingualOutputDescription'),
        model: bilingualOutput
    },
    {
        key: 'addTranslatorInfo',
        title: t('settings.subtitle.addTranslatorInfo'),
        description: t('settings.subtitle.addTranslatorInfoDescription'),
        model: addTranslatorInfo
    },
    {
        key: 'removeLanguageTag',
        title: t('settings.subtitle.removeLanguageTag'),
        description: t('settings.subtitle.removeLanguageTagDescription'),
        model: removeLanguageTag
    },
    {
        key: 'useTagging',
        title: t('settings.subtitle.useTagging'),
        description: t('settings.subtitle.useTaggingDescription'),
        model: useSubtitleTaggingModel
    },
    {
        key: 'validationEnabled',
        title: t('settings.validation.enable'),
        description: t('settings.validation.description'),
        model: validationEnabled
    }
])

interface IValidationRow {
    key: string
    title: string
    model: { value: string }
    presets?: { label: string; value: string }[]
}

const validationRows = computed<IValidationRow[]>(() => {
    if (validationEnabled.value != 'true') return []
    return [
        {
            key: 'minDurationMs',
            title: t('settings.validation.minDuration'),
            model: minDurationMs,
            presets: [
                { label: '0.2s', value: '100' },
                { label: '0.5s', value: '500' },
                { label: '1s', value: '1000' },
                { label: '1.5s', value: '1500' }
            ]
        },
        {
            key: 'maxDurationSecs',
            title: t('settings.validation.maxDuration'),
            model: maxDurationSecs
        },
        {
            key: 'minSubtitleLength',
            title: t('settings.validation.minLength'),
            model: minSubtitleLength
        },
        {
            key: 'maxSubtitleLength',
            title: t('settings.validation.maxLength'),
            model: maxSubtitleLength
        },
        {
            key: 'maxFileSizeBytes',
            title: t('settings.validation.maxSize'),
            model: maxFileSizeBytes,
            presets: [
                { label: '0.5 KB', value: (512 * 1024).toString() },
                { label: '1 MB', value: (1024 * 1024).toString() },
                { label: '1.5 MB', value: Math.round(1.5 * 1024 * 1024).toString() },
                { label: '2 MB', value: (2 * 1024 * 1024).toString() }
            ]
        }
    ]
})
</script>
