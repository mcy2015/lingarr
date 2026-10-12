<template>
    <CardComponent :title="t('settings.translationPrompt.title')">
        <template #description>
            {{ t('settings.translationPrompt.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.translationPrompt.systemPrompt') }}
                    </span>
                    {{ t('settings.translationPrompt.systemPromptDescription') }}
                </div>
                <TextAreaComponent
                    v-model="aiPrompt"
                    :rows="10"
                    :min-height="100"
                    :placeholders="systemPlaceholders"
                    :required-placeholders="['{sourceLanguage}', '{targetLanguage}']"
                    @update:validation="(val) => (isSystemPromptValid = val)" />

                <div v-if="useBatchTranslation !== 'true'" class="space-y-4">
                    <div class="flex flex-col space-x-2">
                        <span class="font-semibold">
                            {{ t('settings.translationPrompt.userPrompt') }}
                        </span>
                        {{ t('settings.translationPrompt.userPromptDescription') }}
                    </div>
                    <TextAreaComponent
                        v-model="aiUserPrompt"
                        :rows="10"
                        :min-height="100"
                        :placeholders="[
                            PLACEHOLDER.LINE_TO_TRANSLATE,
                            PLACEHOLDER.SOURCE_LANGUAGE,
                            PLACEHOLDER.TARGET_LANGUAGE,
                            PLACEHOLDER.CONTEXT_BEFORE,
                            PLACEHOLDER.CONTEXT_AFTER
                        ]"
                        :required-placeholders="['{lineToTranslate}']"
                        @update:validation="(val) => (isUserPromptValid = val)" />

                    <InputComponent
                        v-model="contextBefore"
                        :type="INPUT_TYPE.NUMBER"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        :label="t('settings.translationPrompt.contextBefore')"
                        @update:validation="(val) => (isValid.contextBefore = val)" />
                    <InputComponent
                        v-model="contextAfter"
                        :type="INPUT_TYPE.NUMBER"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        :label="t('settings.translationPrompt.contextAfter')"
                        @update:validation="(val) => (isValid.contextAfter = val)" />

                    <div class="flex flex-col">
                        <span class="font-semibold">
                            {{ t('settings.translationPrompt.structuredContext') }}
                        </span>
                        {{ t('settings.translationPrompt.structuredContextDescription') }}
                    </div>
                    <ToggleButton v-model="useTranslatedContext">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                useTranslatedContext == 'true'
                                    ? t('settings.status.enabled')
                                    : t('settings.status.disabled')
                            }}
                        </span>
                    </ToggleButton>
                </div>
                <div v-else class="text-xs">
                    {{ t('settings.translationPrompt.batchNote') }}
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, PLACEHOLDER, SETTINGS } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import TextAreaComponent from '@/components/common/TextAreaComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import { useToastStore } from '@/store/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const toastStore = useToastStore()
const isSystemPromptValid = ref(false)
const isUserPromptValid = ref(false)
const isValid = reactive({
    contextBefore: true,
    contextAfter: true
})

const useBatchTranslation = computed(
    () => settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string
)

const systemPlaceholders = computed(() => {
    const items = [PLACEHOLDER.SOURCE_LANGUAGE, PLACEHOLDER.TARGET_LANGUAGE]
    if (useBatchTranslation.value !== 'true') {
        items.push(
            PLACEHOLDER.LINE_TO_TRANSLATE,
            PLACEHOLDER.CONTEXT_BEFORE,
            PLACEHOLDER.CONTEXT_AFTER
        )
    }
    return items
})

const aiPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.AI_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_PROMPT, newValue, isSystemPromptValid.value)
        if (isSystemPromptValid.value) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const aiUserPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.AI_USER_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_USER_PROMPT, newValue, isUserPromptValid.value)
        if (isUserPromptValid.value) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const contextBefore = computed({
    get: () => settingsStore.getSetting(SETTINGS.AI_CONTEXT_BEFORE) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_CONTEXT_BEFORE, newValue, isValid.contextBefore)
        if (isValid.contextBefore) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const contextAfter = computed({
    get: () => settingsStore.getSetting(SETTINGS.AI_CONTEXT_AFTER) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_CONTEXT_AFTER, newValue, isValid.contextAfter)
        if (isValid.contextAfter) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const useTranslatedContext = computed({
    get: () => settingsStore.getSetting(SETTINGS.AI_CONTEXT_USE_TRANSLATED) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_CONTEXT_USE_TRANSLATED, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
</script>
