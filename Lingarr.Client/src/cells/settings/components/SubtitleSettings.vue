<template>
    <CardComponent :title="t('settings.subtitle.title')">
        <template #description>
            {{ t('settings.subtitle.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.subtitle.ignoreCaptions') }}
                    </span>
                    {{ t('settings.subtitle.ignoreCaptionsDescription') }}
                </div>
                <ToggleButton v-model="ignoreCaptions">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            ignoreCaptions == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.subtitle.fixOverlapping') }}</span>
                    {{ t('settings.subtitle.fixOverlappingDescription') }}
                </div>
                <ToggleButton v-model="fixOverlappingSubtitles">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            fixOverlappingSubtitles == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.subtitle.stripFormatting') }}</span>
                    {{ t('settings.subtitle.stripFormattingDescription') }}
                </div>
                <ToggleButton v-model="stripSubtitleFormatting">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            stripSubtitleFormatting == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.subtitle.preserveLineBreaks') }}
                    </span>
                    {{ t('settings.subtitle.preserveLineBreaksDescription') }}
                </div>
                <ToggleButton v-model="preserveLineBreaks">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            preserveLineBreaks == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.subtitle.bilingualOutput') }}
                    </span>
                    {{ t('settings.subtitle.bilingualOutputDescription') }}
                </div>
                <ToggleButton v-model="bilingualOutput">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            bilingualOutput == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.subtitle.addTranslatorInfo') }}
                    </span>
                    {{ t('settings.subtitle.addTranslatorInfoDescription') }}
                </div>
                <ToggleButton v-model="addTranslatorInfo">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            addTranslatorInfo == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">
                        {{ t('settings.subtitle.removeLanguageTag') }}
                    </span>
                    {{ t('settings.subtitle.removeLanguageTagDescription') }}
                </div>
                <ToggleButton v-model="removeLanguageTag">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            removeLanguageTag == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>

                <div class="flex flex-col space-y-4">
                    <div class="flex flex-col space-x-2">
                        <span class="font-semibold">{{ t('settings.subtitle.useTagging') }}</span>
                        {{ t('settings.subtitle.useTaggingDescription') }}
                    </div>
                    <ToggleButton v-model="useSubtitleTagging">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                useSubtitleTagging == 'true'
                                    ? t('settings.status.enabled')
                                    : t('settings.status.disabled')
                            }}
                        </span>
                    </ToggleButton>
                    <InputComponent
                        v-if="useSubtitleTagging == 'true'"
                        v-model="subtitleTag"
                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                        :label="t('settings.subtitle.tagLabel')"
                        @update:validation="(val) => (isValid.subtitleTag = val)" />
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'

import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import { useToastStore } from '@/cells/app/stores/toast'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import InputComponent from '@/cells/settings/components/InputComponent.vue'

const toastStore = useToastStore()
const settingsStore = useSettingStore()
const { t } = useI18n()
const isValid = reactive({
    subtitleTag: true
})

const ignoreCaptions = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.IGNORE_CAPTIONS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.IGNORE_CAPTIONS, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const fixOverlappingSubtitles = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const stripSubtitleFormatting = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const preserveLineBreaks = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.PRESERVE_LINE_BREAKS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.PRESERVE_LINE_BREAKS, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const bilingualOutput = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const addTranslatorInfo = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.ADD_TRANSLATOR_INFO) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.ADD_TRANSLATOR_INFO, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const removeLanguageTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.REMOVE_LANGUAGE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.REMOVE_LANGUAGE_TAG, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const useSubtitleTagging = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_SUBTITLE_TAGGING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_SUBTITLE_TAGGING, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const subtitleTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SUBTITLE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SUBTITLE_TAG, newValue, isValid.subtitleTag)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
</script>
