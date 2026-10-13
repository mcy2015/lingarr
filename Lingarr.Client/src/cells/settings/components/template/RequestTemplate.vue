<template>
    <CardComponent :title="t('services.template.title')">
        <template #description>
            {{ t('services.template.description') }}
        </template>
        <template #content>
            <div class="space-y-4">
                <div class="flex flex-wrap items-center justify-between gap-3">
                    <div class="flex flex-wrap items-center gap-2">
                        <span
                            class="text-secondary-content/50 mr-1 text-xs font-medium tracking-wider uppercase">
                            {{ t('services.template.presets') }}
                        </span>
                        <ButtonComponent
                            v-for="preset in presetOptions"
                            :key="preset.value"
                            :variant="selectedPreset === preset.value ? 'secondary' : 'ghost'"
                            size="xs"
                            @click="loadPreset(preset.value)">
                            {{ preset.label }}
                        </ButtonComponent>
                    </div>
                    <div class="border-line inline-flex overflow-hidden rounded-md border">
                        <button
                            class="cursor-pointer px-3 py-1 text-xs font-medium transition-colors duration-200"
                            :class="
                                mode === 'builder'
                                    ? 'bg-accent text-accent-content'
                                    : 'text-secondary-content/70 hover:bg-accent/10 hover:text-accent-content'
                            "
                            @click="mode = 'builder'">
                            {{ t('services.template.builder') }}
                        </button>
                        <button
                            class="border-line cursor-pointer border-l px-3 py-1 text-xs font-medium transition-colors duration-200"
                            :class="
                                mode === 'json'
                                    ? 'bg-accent text-accent-content'
                                    : 'text-secondary-content/70 hover:bg-accent/10 hover:text-accent-content'
                            "
                            @click="mode = 'json'">
                            JSON
                        </button>
                    </div>
                </div>

                <div class="flex items-center gap-2">
                    <span class="text-sm font-semibold">
                        {{ t('services.template.languageFormat') }}
                    </span>
                    <ToggleButton v-model="languageCodeFormat">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                languageCodeFormat === 'true'
                                    ? t('services.template.codeFormat')
                                    : t('services.template.nameFormat')
                            }}
                        </span>
                    </ToggleButton>
                </div>

                <div class="grid grid-cols-1 gap-4 xl:grid-cols-2">
                    <div>
                        <Transition name="fade" mode="out-in">
                            <JsonTreeBuilder
                                v-if="mode === 'builder'"
                                key="builder"
                                :model-value="templateValue"
                                :placeholders="placeholderItems"
                                @update:model-value="onTemplateChange" />
                            <TextAreaComponent
                                v-else
                                key="json"
                                v-model="jsonModel"
                                :rows="16"
                                :placeholders="placeholderItems" />
                        </Transition>
                    </div>
                    <JsonPreview :template="templateValue" :service-type="serviceType" />
                </div>

            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { PLACEHOLDER, SETTINGS, SERVICE_TYPE, ISettings } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import useDebounce from '@/cells/kernel/composables/useDebounce'
import axios from 'axios'
import { requestTemplateService } from '@/cells/settings/services/requestTemplateService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import TextAreaComponent from '@/cells/kernel/components/TextAreaComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import JsonTreeBuilder from './JsonTreeBuilder.vue'
import JsonPreview from './JsonPreview.vue'

const requestTemplateApi = requestTemplateService(axios)
const settingsStore = useSettingStore()
const { t } = useI18n()
const toastStore = useToastStore()
const mode = ref<'builder' | 'json'>('builder')
const jsonText = ref('')
const selectedPreset = ref('')

const placeholderItems = [
    PLACEHOLDER.MODEL,
    PLACEHOLDER.SYSTEM_PROMPT,
    PLACEHOLDER.USER_MESSAGE,
    PLACEHOLDER.SOURCE_LANGUAGE,
    PLACEHOLDER.TARGET_LANGUAGE,
    PLACEHOLDER.LINE_TO_TRANSLATE,
    PLACEHOLDER.CONTEXT_BEFORE,
    PLACEHOLDER.CONTEXT_AFTER
]

const props = defineProps<{ service: string }>()

const serviceType = computed(() => props.service)

const languageCodeFormat = computed({
    get: (): string =>
        (settingsStore.getSetting(SETTINGS.LANGUAGE_CODE_FORMAT) as string) ?? 'false',
    set: (value: string) => {
        settingsStore.updateSetting(SETTINGS.LANGUAGE_CODE_FORMAT, value, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const templateMap: Record<string, keyof ISettings> = {
    [SERVICE_TYPE.OPENAI]: SETTINGS.OPENAI_REQUEST_TEMPLATE,
    [SERVICE_TYPE.ANTHROPIC]: SETTINGS.ANTHROPIC_REQUEST_TEMPLATE,
    [SERVICE_TYPE.LOCALAI]: SETTINGS.LOCAL_AI_CHAT_REQUEST_TEMPLATE,
    [SERVICE_TYPE.GEMINI]: SETTINGS.GEMINI_REQUEST_TEMPLATE,
    [SERVICE_TYPE.DEEPSEEK]: SETTINGS.DEEPSEEK_REQUEST_TEMPLATE,
    [SERVICE_TYPE.MISTRAL]: SETTINGS.MISTRAL_REQUEST_TEMPLATE,
    [SERVICE_TYPE.XAI]: SETTINGS.XAI_REQUEST_TEMPLATE
}

const localAiEndpoint = computed(
    () => settingsStore.getSetting(SETTINGS.LOCAL_AI_ENDPOINT) as string
)
const isLocalAiGenerate = computed(() => {
    const endpoint = localAiEndpoint.value || ''
    return !endpoint.trimEnd().replace(/\/$/, '').endsWith('completions')
})

const activeTemplateKey = computed((): keyof ISettings | '' => {
    if (serviceType.value === SERVICE_TYPE.LOCALAI) {
        return isLocalAiGenerate.value
            ? SETTINGS.LOCAL_AI_GENERATE_REQUEST_TEMPLATE
            : SETTINGS.LOCAL_AI_CHAT_REQUEST_TEMPLATE
    }
    return templateMap[serviceType.value] || ''
})

const templateValue = computed(() => {
    if (!activeTemplateKey.value) {
        return '{}'
    }
    return (settingsStore.getSetting(activeTemplateKey.value) as string) || '{}'
})

const prettyTemplate = computed(() => {
    try {
        return JSON.stringify(JSON.parse(templateValue.value), null, 2)
    } catch {
        return templateValue.value
    }
})

const jsonModel = computed({
    get: () => jsonText.value || prettyTemplate.value,
    set: (value: string) => onJsonInput(value)
})

const debouncedSave = useDebounce((key: keyof ISettings, value: string) => {
    settingsStore.updateSetting(key, value, true)
    toastStore.push({ message: t('common.saved'), type: 'success' })
}, 500)

function onTemplateChange(value: string) {
    const key = activeTemplateKey.value
    if (!key) {
        return
    }
    debouncedSave(key, value)
}

function onJsonInput(value: string) {
    jsonText.value = value
    const key = activeTemplateKey.value
    if (!key) {
        return
    }
    try {
        const parsed = JSON.parse(value)
        const compact = JSON.stringify(parsed)
        debouncedSave(key, compact)
    } catch {
        // Don't save invalid JSON (user could be still typing)
    }
}

const presets = ref<Record<string, string>>({})
const presetOptions = ref<{ value: string; label: string }[]>([])

const presetLabelMap: Record<string, string> = {
    openai_request_template: 'OpenAI Chat',
    anthropic_request_template: 'Anthropic Messages',
    gemini_request_template: 'Gemini Content',
    local_ai_generate_request_template: 'Ollama Generate',
    local_ai_chat_request_template: 'LocalAI Chat',
    deepseek_request_template: 'DeepSeek Chat',
    mistral_request_template: 'Mistral Chat',
    xai_request_template: 'xAI Chat'
}

onMounted(async () => {
    try {
        const defaults = await requestTemplateApi.getDefaults<Record<string, string>>()
        presets.value = defaults
        presetOptions.value = Object.keys(defaults).map((key) => ({
            value: key,
            label: presetLabelMap[key] || key
        }))
    } catch {
        // Ignore failed default retrieval
    }
})

function loadPreset(presetKey: string) {
    const template = presets.value[presetKey]
    const key = activeTemplateKey.value
    if (!template || !key) {
        return
    }
    selectedPreset.value = presetKey
    settingsStore.updateSetting(key, template, true)
    toastStore.push({ message: t('common.saved'), type: 'success' })
    if (mode.value === 'json') {
        syncJsonText()
    }
}

function syncJsonText() {
    jsonText.value = prettyTemplate.value
}

/// ── pb-head actions(原型 screen-10):保存 = 立即落库当前模版并按
/// 「新批次起生效」语义 toast(JSON 模式下未保存的合法 JSON 一并冲刷);
/// 重置 = 恢复该服务当前模版键对应的默认预设。
const canReset = computed(() => {
    const key = activeTemplateKey.value
    return !!key && !!presets.value[key]
})

function save(): boolean {
    const key = activeTemplateKey.value
    if (!key) {
        return false
    }
    if (mode.value === 'json') {
        try {
            const compact = JSON.stringify(JSON.parse(jsonText.value || prettyTemplate.value))
            settingsStore.updateSetting(key, compact, true)
        } catch (error) {
            toastStore.push({
                message: t('toast.template.invalidJson', {
                    message: error instanceof Error ? error.message : String(error)
                }),
                type: 'error'
            })
            return false
        }
    } else {
        settingsStore.updateSetting(key, templateValue.value, true)
    }
    toastStore.push({ message: t('requestTemplate.savedToast'), type: 'success' })
    return true
}

function resetToDefault(): boolean {
    const key = activeTemplateKey.value
    const preset = key ? presets.value[key] : undefined
    if (!key || !preset) {
        return false
    }
    selectedPreset.value = key
    settingsStore.updateSetting(key, preset, true)
    if (mode.value === 'json') {
        syncJsonText()
    }
    toastStore.push({ message: t('requestTemplate.resetToast'), type: 'success' })
    return true
}

defineExpose({ save, resetToDefault, canReset })

watch(mode, (newMode) => {
    if (newMode === 'json') {
        syncJsonText()
    }
})

watch(activeTemplateKey, () => {
    syncJsonText()
})

watch(serviceType, () => {
    selectedPreset.value = ''
})
</script>
