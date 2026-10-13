<template>
    <CardComponent :title="t('services.integration.title')">
        <template #description>{{ t('services.integration.description') }}</template>
        <template #content>
            <div class="flex flex-col space-y-2">
                <span class="font-semibold">{{ t('services.integration.radarrSettings') }}</span>
                <InputComponent
                    v-model="radarrUrl"
                    :validation-type="INPUT_VALIDATION_TYPE.URL"
                    :label="t('services.integration.address')"
                    :error-message="t('services.integration.radarrUrlError')"
                    @update:validation="(val) => (isValid.radarrUrl = val)" />
                <InputComponent
                    v-model="radarrApiKey"
                    :min-length="32"
                    :max-length="32"
                    :validation-type="INPUT_VALIDATION_TYPE.STRING"
                    :type="INPUT_TYPE.PASSWORD"
                    :label="t('services.integration.apiKey')"
                    :error-message="t('services.integration.apiKeyLengthError', { minLength: 32 })"
                    @update:validation="(val) => (isValid.radarrApiKey = val)" />
                <div class="flex items-center space-x-2">
                    <ToggleButton v-model="radarrDefaultInclude">
                        <span class="text-primary-content text-sm font-medium">
                            {{ t('services.integration.includeByDefault') }}
                        </span>
                    </ToggleButton>
                </div>
            </div>
            <div class="flex flex-col space-y-2">
                <span class="font-semibold">{{ t('services.integration.sonarrSettings') }}</span>
                <InputComponent
                    v-model="sonarrUrl"
                    :validation-type="INPUT_VALIDATION_TYPE.URL"
                    :label="t('services.integration.address')"
                    :error-message="t('services.integration.sonarrUrlError')"
                    @update:validation="(val) => (isValid.sonarrUrl = val)" />
                <InputComponent
                    v-model="sonarrApiKey"
                    :min-length="32"
                    :max-length="32"
                    :validation-type="INPUT_VALIDATION_TYPE.STRING"
                    :type="INPUT_TYPE.PASSWORD"
                    :label="t('services.integration.apiKey')"
                    :error-message="t('services.integration.apiKeyLengthError', { minLength: 32 })"
                    @update:validation="(val) => (isValid.sonarrApiKey = val)" />
                <div class="flex items-center space-x-2">
                    <ToggleButton v-model="sonarrDefaultInclude">
                        <span class="text-primary-content text-sm font-medium">
                            {{ t('services.integration.includeByDefault') }}
                        </span>
                    </ToggleButton>
                </div>
            </div>
            <div>
                {{ t('services.integration.noMediaHint') }}
                <router-link to="/settings/tasks" class="underline">
                    {{ t('services.integration.here') }}
                </router-link>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, SETTINGS, ENCRYPTED_SETTINGS } from '@/types'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

const isValid = reactive({
    radarrUrl: false,
    radarrApiKey: false,
    sonarrUrl: false,
    sonarrApiKey: false
})
const { t } = useI18n()
const toastStore = useToastStore()
const settingsStore = useSettingStore()

const radarrApiKey = computed({
    get: (): string | null =>
        settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.RADARR_API_KEY) ?? null,
    set: (newValue: string): void => {
        settingsStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.RADARR_API_KEY,
            newValue,
            isValid.radarrApiKey
        )
        if (isValid.radarrApiKey) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const sonarrApiKey = computed({
    get: (): string | null =>
        settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.SONARR_API_KEY) ?? null,
    set: (newValue: string): void => {
        settingsStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.SONARR_API_KEY,
            newValue,
            isValid.sonarrApiKey
        )
        if (isValid.sonarrApiKey) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const radarrUrl = computed({
    get: (): string | null => (settingsStore.getSetting(SETTINGS.RADARR_URL) as string) ?? null,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RADARR_URL, newValue, isValid.radarrUrl)
        if (isValid.radarrUrl) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const sonarrUrl = computed({
    get: (): string | null => (settingsStore.getSetting(SETTINGS.SONARR_URL) as string) ?? null,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SONARR_URL, newValue, isValid.sonarrUrl)
        if (isValid.sonarrUrl) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const radarrDefaultInclude = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RADARR_DEFAULT_INCLUDE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RADARR_DEFAULT_INCLUDE, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
const sonarrDefaultInclude = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SONARR_DEFAULT_INCLUDE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SONARR_DEFAULT_INCLUDE, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
</script>
