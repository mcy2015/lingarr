<template>
    <!-- ═══ API Key 区卡(原型 screen-06 setlist 行式):生成动作行 + key 的
         mono 展示 + 用法说明(curl 示例)。authEnabled 时可见,生成走
         /api/auth 的既有端点,写回加密设置(数据流零变化)。 -->
    <div v-if="authEnabled == 'true'" class="border-line bg-secondary rounded-md border" data-test="apikey-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.apiKey.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.apiKey.description') }}
            </span>
        </div>

        <div class="space-y-3 px-4 py-3">
            <div class="flex flex-wrap items-center gap-x-3 gap-y-2 text-[13px]">
                <b class="min-w-0 flex-1 font-semibold">
                    {{ t('settings.apiKey.label') }}
                </b>
                <ButtonComponent
                    v-if="apiKey"
                    variant="secondary"
                    size="sm"
                    :loading="revoking"
                    data-test="apikey-revoke"
                    @click="revokeApiKey()">
                    {{ revoking ? t('settings.apiKey.revoking') : t('settings.apiKey.revoke') }}
                </ButtonComponent>
                <ButtonComponent
                    variant="primary"
                    size="sm"
                    :loading="loading"
                    data-test="apikey-generate"
                    @click="generateApiKey()">
                    {{ loading ? t('settings.apiKey.generating') : t('settings.apiKey.generate') }}
                </ButtonComponent>
            </div>

            <code
                class="mono border-line bg-primary block overflow-x-auto rounded-md border p-3 text-xs break-all text-success"
                data-test="apikey-value">
                {{ apiKey }}
            </code>

            <div class="border-line bg-tertiary rounded-md border p-4">
                <h3 class="mb-2 text-[13px] font-semibold">
                    {{ t('settings.apiKey.howToUse') }}
                </h3>
                <p class="text-secondary-content mb-2.5 text-xs leading-relaxed">
                    {{ t('settings.apiKey.headerIntro') }}
                    <code class="mono rounded bg-primary px-1.5 py-0.5 text-success">
                        X-Api-Key
                    </code>
                    {{ t('settings.apiKey.headerSuffix') }}
                </p>
                <code
                    class="mono border-line bg-primary block overflow-x-auto rounded-md border p-3 text-xs break-all text-success">
                    curl -H "X-Api-Key: {{ apiKey || 'API-KEY' }}"
                    http://lingarr:9876/api/endpoint
                </code>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { authService } from '@/cells/app/api/authService'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import { useToastStore } from '@/cells/app/stores/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const authApi = authService(axios)

const toastStore = useToastStore()
const loading = ref(false)
const revoking = ref(false)

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
    loading.value = true
    try {
        const response = await authApi.generateApiKey()
        await settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY, response.apiKey)
    } finally {
        loading.value = false
    }
}

/// P2-4: 吊销 = 服务端清空加密 api_key(空串即失效),本地展示同步清空。
/// 沿用 /api/auth 的既有加密写回通道,无需新的设置语义。
const revokeApiKey = async () => {
    revoking.value = true
    try {
        await authApi.revokeApiKey()
        await settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY, '')
        toastStore.push({ message: t('settings.apiKey.revoked'), type: 'success' })
    } finally {
        revoking.value = false
    }
}
</script>
