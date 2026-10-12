<template>
    <CardComponent :title="t('settings.proofread.title')">
        <template #description>
            {{ t('settings.proofread.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.proofread.systemPrompt') }}</span>
                    {{ t('settings.proofread.systemPromptDescription') }}
                </div>
                <TextAreaComponent
                    v-model="proofreadPrompt"
                    :rows="10"
                    :min-height="100"
                    :placeholders="[PLACEHOLDER.SOURCE_LANGUAGE, PLACEHOLDER.TARGET_LANGUAGE]"
                    :required-placeholders="['{sourceLanguage}', '{targetLanguage}']"
                    @update:validation="(val) => (isSystemPromptValid = val)" />

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.proofread.userPrompt') }}</span>
                    {{ t('settings.proofread.userPromptDescription') }}
                </div>
                <TextAreaComponent
                    v-model="proofreadUserPrompt"
                    :rows="6"
                    :min-height="60"
                    :placeholders="[PLACEHOLDER.SOURCE_LINE, PLACEHOLDER.TRANSLATED_LINE]"
                    :required-placeholders="['{sourceLine}', '{translatedLine}']"
                    @update:validation="(val) => (isUserPromptValid = val)" />
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { PLACEHOLDER, SETTINGS } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import TextAreaComponent from '@/components/common/TextAreaComponent.vue'
import { useToastStore } from '@/store/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const toastStore = useToastStore()
const isSystemPromptValid = ref(false)
const isUserPromptValid = ref(false)

const proofreadPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.PROOFREAD_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.PROOFREAD_PROMPT, newValue, isSystemPromptValid.value)
        if (isSystemPromptValid.value) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const proofreadUserPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.PROOFREAD_USER_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.PROOFREAD_USER_PROMPT,
            newValue,
            isUserPromptValid.value
        )
        if (isUserPromptValid.value) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
</script>
