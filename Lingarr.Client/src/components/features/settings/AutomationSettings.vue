<template>
    <CardComponent :title="t('settings.automation.indexingTitle')">
        <template #description>
            {{ t('settings.automation.indexingDescription') }}
        </template>
        <template #content>
            <SaveNotification ref="saveNotification" />
            <div class="flex flex-col space-y-2 pb-4">
                <span class="font-semibold">{{ t('settings.automation.movieIndexer') }}</span>
                <InputComponent
                    v-model="movieSchedule"
                    :label="t('settings.automation.cronFormat')"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (movieScheduleIsValid = val)" />
                <span class="font-semibold">{{ t('settings.automation.showIndexer') }}</span>
                <InputComponent
                    v-model="showSchedule"
                    :label="t('settings.automation.cronFormat')"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (showScheduleIsValid = val)" />
            </div>
        </template>
    </CardComponent>

    <CardComponent :title="t('settings.automation.title')">
        <template #description>
            {{ t('settings.automation.description') }}
            <a class="cursor-pointer underline" @click="router.push({ name: 'services-settings' })">
                {{ t('settings.automation.servicesLink') }}
            </a>
            {{ t('settings.automation.descriptionSuffix') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <div class="flex items-center space-x-2">
                    <span>{{ t('settings.automation.automatedTranslation') }}</span>
                    <ToggleButton v-model="automationEnabled">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                automationEnabled === 'true'
                                    ? t('settings.status.enabled')
                                    : t('settings.status.disabled')
                            }}
                        </span>
                    </ToggleButton>
                </div>

                <span class="font-semibold">
                    {{ t('settings.automation.translationSchedule') }}
                </span>
                <InputComponent
                    v-model="translationSchedule"
                    :label="t('settings.automation.cronFormat')"
                    :placeholder="'0 * * * *'"
                    :validation-type="INPUT_VALIDATION_TYPE.CRON"
                    @update:validation="(val) => (translationScheduleIsValid = val)" />

                <span class="font-semibold">{{ t('settings.automation.limits') }}</span>
                <InputComponent
                    v-model="maxTranslationsPerRun"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    :label="t('settings.automation.limitPerSchedule')"
                    @update:validation="(val) => (maxTranslationsPerRunIsValid = val)" />

                <span class="font-semibold">{{ t('settings.automation.fileAgeDelay') }}</span>
                <InputComponent
                    v-model="movieAgeThreshold"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    :label="t('settings.automation.movieAgeDelay')"
                    @update:validation="(val) => (movieAgeThresholdIsValid = val)" />
                <InputComponent
                    v-model="showAgeThreshold"
                    :type="INPUT_TYPE.NUMBER"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    :min-length="0"
                    :label="t('settings.automation.showAgeDelay')"
                    @update:validation="(val) => (showAgeThresholdIsValid = val)" />
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { useRouter } from 'vue-router'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const maxTranslationsPerRunIsValid = ref(false)
const movieAgeThresholdIsValid = ref(false)
const showAgeThresholdIsValid = ref(false)
const movieScheduleIsValid = ref(false)
const showScheduleIsValid = ref(false)
const translationScheduleIsValid = ref(false)
const settingsStore = useSettingStore()
const router = useRouter()
const { t } = useI18n()

const automationEnabled = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.AUTOMATION_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.AUTOMATION_ENABLED, newValue, true)
        saveNotification.value?.show()
    }
})
const movieSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MOVIE_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (movieScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.MOVIE_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const showSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SHOW_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (showScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.SHOW_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const translationSchedule = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.TRANSLATION_SCHEDULE) as string,
    set: (newValue: string): void => {
        if (translationScheduleIsValid.value) {
            settingsStore.updateSetting(SETTINGS.TRANSLATION_SCHEDULE, newValue, true)
            saveNotification.value?.show()
        }
    }
})
const maxTranslationsPerRun = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_TRANSLATIONS_PER_RUN, newValue, true)
        saveNotification.value?.show()
    }
})

const movieAgeThreshold = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MOVIE_AGE_THRESHOLD) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MOVIE_AGE_THRESHOLD, newValue, true)
        saveNotification.value?.show()
    }
})

const showAgeThreshold = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SHOW_AGE_THRESHOLD) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SHOW_AGE_THRESHOLD, newValue, true)
        saveNotification.value?.show()
    }
})
</script>
