<template>
    <!-- ═══ API Key 区卡(敏感值统一策略样板):卡头唯一「API 密钥」标题 +
         状态语义(已生成/未生成),值行默认掩码(前4+•••+后4,短值全掩码),
         眼睛按钮显/隐、复制按钮写入完整值;掩码态 DOM 不含完整明文。
         用法折叠区 curl 恒占位符。生成/吊销走 /api/auth 既有端点(数据流零变化)。 -->
    <div
        v-if="authEnabled == 'true'"
        class="border-line bg-secondary rounded-md border"
        data-test="apikey-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.apiKey.title') }}</h2>
            <span
                class="mono ml-auto rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                :class="
                    apiKey
                        ? 'border-success/40 bg-success/10 text-success'
                        : 'border-line bg-tertiary text-secondary-content'
                "
                data-test="apikey-status">
                {{
                    apiKey
                        ? t('settings.apiKey.statusGenerated')
                        : t('settings.apiKey.statusNotGenerated')
                }}
            </span>
        </div>

        <div class="space-y-3 px-4 py-3">
            <!-- 值行:掩码显示 + 显/隐 + 复制(完整值仅经剪贴板离开,不进 DOM 明文态) -->
            <div class="flex flex-wrap items-center gap-2">
                <code
                    class="mono border-line bg-primary min-w-0 flex-1 overflow-x-auto rounded-md border p-3 text-xs break-all"
                    :class="apiKey ? 'text-success' : 'text-secondary-content'"
                    data-test="apikey-value">
                    {{ displayValue }}
                </code>
                <button
                    v-if="apiKey"
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1.5 text-xs transition-colors"
                    :title="revealed ? t('settings.apiKey.hide') : t('settings.apiKey.show')"
                    data-test="apikey-visibility"
                    @click="revealed = !revealed">
                    <Icon :name="revealed ? 'eyeOn' : 'eyeOff'" class="h-4 w-4" />
                </button>
                <button
                    v-if="apiKey"
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1.5 text-xs transition-colors"
                    :title="t('settings.apiKey.copy')"
                    data-test="apikey-copy"
                    @click="copyApiKey()">
                    {{ t('settings.apiKey.copy') }}
                </button>
            </div>

            <div class="flex flex-wrap items-center gap-2">
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

            <!-- 用法折叠区:curl 恒占位符,任何状态不内嵌真实 key -->
            <div class="border-line bg-tertiary rounded-md border p-4">
                <button
                    type="button"
                    class="text-secondary-content hover:text-primary-content flex w-full cursor-pointer items-center gap-2 text-left text-[13px] font-semibold transition-colors"
                    :aria-expanded="usageOpen"
                    data-test="apikey-usage-fold"
                    @click="usageOpen = !usageOpen">
                    <span class="text-xs">{{ usageOpen ? '▾' : '▸' }}</span>
                    {{ t('settings.apiKey.howToUse') }}
                </button>
                <div v-if="usageOpen" class="mt-2.5">
                    <p class="text-secondary-content mb-2.5 text-xs leading-relaxed">
                        {{ t('settings.apiKey.headerIntro') }}
                        <code class="mono bg-primary text-success rounded px-1.5 py-0.5">
                            X-Api-Key
                        </code>
                        {{ t('settings.apiKey.headerSuffix') }}
                    </p>
                    <code
                        class="mono border-line bg-primary text-success block overflow-x-auto rounded-md border p-3 text-xs break-all"
                        data-test="apikey-curl">
                        curl -H "X-Api-Key: YOUR_API_KEY" http://lingarr:9876/api/endpoint
                    </code>
                </div>
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
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import { maskSecret } from '@/cells/kernel/utils/string'
import { useToastStore } from '@/cells/app/stores/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const authApi = authService(axios)

const toastStore = useToastStore()
const loading = ref(false)
const revoking = ref(false)

/// 敏感值统一策略:默认掩码(短值全掩码),显/隐仅切换渲染文本;
/// 掩码态下完整明文不进入 DOM(值不在模板、不在属性,仅存于内存与剪贴板)。
const revealed = ref(false)
const usageOpen = ref(false)

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

const displayValue = computed(() => {
    const value = apiKey.value ?? ''
    if (value === '') return '—'
    return revealed.value ? value : maskSecret(value)
})

const copyApiKey = async () => {
    const value = apiKey.value ?? ''
    if (value === '') return
    try {
        await navigator.clipboard.writeText(value)
        toastStore.push({ message: t('settings.apiKey.copied'), type: 'success' })
    } catch {
        toastStore.push({ message: t('settings.apiKey.copyFailed'), type: 'error' })
    }
}

const generateApiKey = async () => {
    loading.value = true
    try {
        const response = await authApi.generateApiKey()
        revealed.value = false
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
        revealed.value = false
        await authApi.revokeApiKey()
        await settingsStore.updateEncryptedSetting(ENCRYPTED_SETTINGS.API_KEY, '')
        toastStore.push({ message: t('settings.apiKey.revoked'), type: 'success' })
    } finally {
        revoking.value = false
    }
}
</script>
