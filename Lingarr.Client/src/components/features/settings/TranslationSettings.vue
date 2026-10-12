<template>
    <CardComponent :title="t('settings.translation.title')">
        <template #description>
            {{ t('settings.translation.description') }}
        </template>
        <template #content>

            <template
                v-if="
                    [
                        SERVICE_TYPE.ANTHROPIC,
                        SERVICE_TYPE.DEEPSEEK,
                        SERVICE_TYPE.GEMINI,
                        SERVICE_TYPE.LOCALAI,
                        SERVICE_TYPE.MISTRAL,
                        SERVICE_TYPE.OPENAI,
                        SERVICE_TYPE.XAI
                    ].includes(
                        serviceType as
                            | 'openai'
                            | 'anthropic'
                            | 'localai'
                            | 'gemini'
                            | 'deepseek'
                            | 'mistral'
                            | 'xai'
                    )
                ">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.translation.useBatch') }}</span>
                    {{ t('settings.translation.useBatchDescription') }}
                </div>
                <ToggleButton v-model="useBatchTranslation">
                    <span class="text-primary-content text-sm font-medium">
                        {{
                            useBatchTranslation == 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>
            </template>

            <template v-if="useBatchTranslation == 'true'">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">{{ t('settings.translation.batchSize') }}</span>
                    {{ t('settings.translation.batchSizeDescription') }}
                </div>
                <InputComponent
                    v-model="maxBatchSize"
                    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                    @update:validation="(val) => (isValid.maxBatchSize = val)" />
            </template>

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">{{ t('settings.translation.requestTimeout') }}</span>
                {{ t('settings.translation.requestTimeoutDescription') }}
            </div>
            <InputComponent
                v-model="requestTimeout"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.requestTimeout = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">{{ t('settings.translation.maxRetries') }}</span>
                {{ t('settings.translation.maxRetriesDescription') }}
            </div>
            <InputComponent
                v-model="maxRetries"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.maxRetries = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">{{ t('settings.translation.retryDelay') }}</span>
                {{ t('settings.translation.retryDelayDescription') }}
            </div>
            <InputComponent
                v-model="retryDelay"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.retryDelay = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">
                    {{ t('settings.translation.retryDelayMultiplier') }}
                </span>
                {{ t('settings.translation.retryDelayMultiplierDescription') }}
            </div>
            <InputComponent
                v-model="retryDelayMultiplier"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.retryDelayMultiplier = val)" />
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { INPUT_VALIDATION_TYPE, SERVICE_TYPE, SETTINGS } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import { useToastStore } from '@/store/toast'
import InputComponent from '@/components/common/InputComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'

const toastStore = useToastStore()
const settingsStore = useSettingStore()
const { t } = useI18n()
const isValid = reactive({
    maxBatchSize: true,
    requestTimeout: true,
    maxRetries: true,
    retryDelay: true,
    retryDelayMultiplier: true
})

const serviceType = computed(() => {
    try {
        const raw = settingsStore.getSetting(SETTINGS.SERVICE_TYPE) as string
        const services: string[] = JSON.parse(raw)
        return services[0] ?? ''
    } catch {
        return ''
    }
})

const useBatchTranslation = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_BATCH_TRANSLATION, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const maxBatchSize = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_BATCH_SIZE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_BATCH_SIZE, newValue, isValid.maxBatchSize)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const requestTimeout = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.REQUEST_TIMEOUT) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.REQUEST_TIMEOUT, newValue, isValid.requestTimeout)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const maxRetries = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_RETRIES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_RETRIES, newValue, isValid.maxRetries)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const retryDelay = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RETRY_DELAY, newValue, isValid.retryDelay)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const retryDelayMultiplier = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY_MULTIPLIER) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(
            SETTINGS.RETRY_DELAY_MULTIPLIER,
            newValue,
            isValid.retryDelayMultiplier
        )
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
</script>
