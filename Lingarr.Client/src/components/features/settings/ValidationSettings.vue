<template>
    <CardComponent :title="t('settings.validation.title')">
        <template #description>
            {{ t('settings.validation.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.validation.enable') }}</span>
                </div>
                <ToggleButton v-model="validationEnabled">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            validationEnabled == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <InputComponent
                    v-if="validationEnabled == 'true'"
                    v-model="minDurationMs"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :label="t('settings.validation.minDuration')"
                    @update:validation="(val) => (isValid.minDurationMs = val)">
                    <div class="flex flex-wrap gap-2">
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="minDurationMs = '100'">
                            0.2s
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="minDurationMs = '500'">
                            0.5s
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="minDurationMs = '1000'">
                            1s
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="minDurationMs = '1500'">
                            1.5s
                        </button>
                    </div>
                </InputComponent>

                <InputComponent
                    v-if="validationEnabled == 'true'"
                    v-model="maxDurationSecs"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :label="t('settings.validation.maxDuration')"
                    @update:validation="(val) => (isValid.maxDurationSecs = val)" />

                <InputComponent
                    v-if="validationEnabled == 'true'"
                    v-model="minSubtitleLength"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :label="t('settings.validation.minLength')"
                    @update:validation="(val) => (isValid.minSubtitleLength = val)" />

                <InputComponent
                    v-if="validationEnabled == 'true'"
                    v-model="maxSubtitleLength"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :label="t('settings.validation.maxLength')"
                    @update:validation="(val) => (isValid.maxSubtitleLength = val)" />

                <InputComponent
                    v-if="validationEnabled == 'true'"
                    v-model="maxFileSizeBytes"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :label="t('settings.validation.maxSize')"
                    @update:validation="(val) => (isValid.maxFileSizeBytes = val)">
                    <div class="flex flex-wrap gap-2">
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="maxFileSizeBytes = (512 * 1024).toString()">
                            0.5 KB
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="maxFileSizeBytes = (1024 * 1024).toString()">
                            1 MB
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="maxFileSizeBytes = (1.5 * 1024 * 1024).toString()">
                            1.5 MB
                        </button>
                        <button
                            type="button"
                            class="border-line hover:bg-accent cursor-pointer rounded border px-2 py-1 text-xs transition-colors hover:text-primary-content"
                            @click="maxFileSizeBytes = (2 * 1024 * 1024).toString()">
                            2 MB
                        </button>
                    </div>
                </InputComponent>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { reactive, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/store/setting'

import CardComponent from '@/components/common/CardComponent.vue'
import { useToastStore } from '@/store/toast'
import ToggleButton from '@/components/common/ToggleButton.vue'
import InputComponent from '@/components/common/InputComponent.vue'

const toastStore = useToastStore()
const settingsStore = useSettingStore()
const { t } = useI18n()

const isValid = reactive({
    maxDurationSecs: true,
    minDurationMs: true,
    minSubtitleLength: true,
    maxSubtitleLength: true,
    maxFileSizeBytes: true
})

const validationEnabled = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SUBTITLE_VALIDATION_ENABLED, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const maxDurationSecs = computed({
    get: () => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_MAXDURATIONSECS) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.SUBTITLE_VALIDATION_MAXDURATIONSECS,
            newValue,
            isValid.maxDurationSecs
        )
        if (isValid.maxDurationSecs) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const minDurationMs = computed({
    get: () => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_MINDURATIONMS) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.SUBTITLE_VALIDATION_MINDURATIONMS,
            newValue,
            isValid.minDurationMs
        )
        if (isValid.minDurationMs) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const minSubtitleLength = computed({
    get: () => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_MINSUBTITLELENGTH) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.SUBTITLE_VALIDATION_MINSUBTITLELENGTH,
            newValue,
            isValid.minSubtitleLength
        )
        if (isValid.minSubtitleLength) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const maxSubtitleLength = computed({
    get: () => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_MAXSUBTITLELENGTH) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.SUBTITLE_VALIDATION_MAXSUBTITLELENGTH,
            newValue,
            isValid.maxSubtitleLength
        )
        if (isValid.maxSubtitleLength) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})

const maxFileSizeBytes = computed({
    get: () => settingsStore.getSetting(SETTINGS.SUBTITLE_VALIDATION_MAXFILESIZEBYTES) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(
            SETTINGS.SUBTITLE_VALIDATION_MAXFILESIZEBYTES,
            newValue,
            isValid.maxFileSizeBytes
        )
        if (isValid.maxFileSizeBytes) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
</script>
