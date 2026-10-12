<template>
    <CardComponent v-if="authEnabled == 'true'" :title="t('settings.apiKey.title')">
        <template #description></template>
        <template #content>
            <div class="flex flex-col space-y-2 pb-4">
                <div>
                    <h2 class="mb-4 text-xl font-semibold text-primary-content">{{ title }}</h2>
                    <p class="mb-6 text-sm text-secondary-content/60">
                        {{ t('settings.apiKey.description') }}
                    </p>
                </div>

                <div class="space-y-4">
                    <ButtonComponent variant="primary" :loading="loading" @click="generateApiKey()">
                        {{
                            loading
                                ? t('settings.apiKey.generating')
                                : t('settings.apiKey.generate')
                        }}
                    </ButtonComponent>

                    <div class="rounded-lg bg-tertiary p-4">
                        <div class="mb-2 flex items-center justify-between">
                            <label class="text-sm font-medium text-secondary-content/60">
                                {{ t('settings.apiKey.label') }}
                            </label>
                        </div>
                        <code
                            class="block rounded bg-secondary p-3 font-mono text-sm break-all text-success">
                            {{ apiKey }}
                        </code>
                    </div>

                    <div class="rounded-lg border border-line bg-tertiary p-6">
                        <h3 class="mb-3 text-lg font-semibold text-primary-content">
                            {{ t('settings.apiKey.howToUse') }}
                        </h3>
                        <p class="mb-3 text-sm text-secondary-content/60">
                            {{ t('settings.apiKey.headerIntro') }}
                            <code class="rounded bg-secondary px-2 py-1 text-success">
                                X-Api-Key
                            </code>
                            {{ t('settings.apiKey.headerSuffix') }}
                        </p>
                        <code
                            class="block rounded bg-secondary p-3 font-mono text-xs break-all text-success">
                            curl -H "X-Api-Key: {{ apiKey || 'API-KEY' }}"
                            http://lingarr:9876/api/endpoint
                        </code>
                    </div>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'
import { useSettingStore } from '@/store/setting'
import services from '@/services'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import CardComponent from '@/components/common/CardComponent.vue'
import { useToastStore } from '@/store/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const { title } = defineProps<{
    title?: string
}>()

const toastStore = useToastStore()
const loading = ref(false)

const authEnabled = computed({
    get: () => settingsStore.getSetting(SETTINGS.AUTH_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.AUTH_ENABLED, newValue.toString(), true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const apiKey = computed({
    get: () => settingsStore.getEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY),
    set: (newValue: string): void => {
        settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY, newValue.toString())
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const generateApiKey = async () => {
    const response = await services.auth.generateApiKey()
    await settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY, response.apiKey)
}
</script>
