<template>
    <CardComponent :title="t('settings.subtitle.title')">
        <template #description>
            {{ t('settings.subtitle.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <SaveNotification ref="saveNotification" />
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
import { ref, computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/ts'
import { useSettingStore } from '@/store/setting'

import CardComponent from '@/components/common/CardComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import InputComponent from '@/components/common/InputComponent.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const settingsStore = useSettingStore()
const { t } = useI18n()
const isValid = reactive({
    subtitleTag: true
})

const ignoreCaptions = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.IGNORE_CAPTIONS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.IGNORE_CAPTIONS, newValue, true)
        saveNotification.value?.show()
    }
})

const fixOverlappingSubtitles = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.FIX_OVERLAPPING_SUBTITLES, newValue, true)
        saveNotification.value?.show()
    }
})

const stripSubtitleFormatting = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.STRIP_SUBTITLE_FORMATTING, newValue, true)
        saveNotification.value?.show()
    }
})

const preserveLineBreaks = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.PRESERVE_LINE_BREAKS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.PRESERVE_LINE_BREAKS, newValue, true)
        saveNotification.value?.show()
    }
})

const bilingualOutput = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.BILINGUAL_SUBTITLE_OUTPUT, newValue, true)
        saveNotification.value?.show()
    }
})

const addTranslatorInfo = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.ADD_TRANSLATOR_INFO) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.ADD_TRANSLATOR_INFO, newValue, true)
        saveNotification.value?.show()
    }
})

const removeLanguageTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.REMOVE_LANGUAGE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.REMOVE_LANGUAGE_TAG, newValue, true)
        saveNotification.value?.show()
    }
})

const useSubtitleTagging = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_SUBTITLE_TAGGING) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_SUBTITLE_TAGGING, newValue, true)
        saveNotification.value?.show()
    }
})

const subtitleTag = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SUBTITLE_TAG) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SUBTITLE_TAG, newValue, isValid.subtitleTag)
        saveNotification.value?.show()
    }
})
</script>
