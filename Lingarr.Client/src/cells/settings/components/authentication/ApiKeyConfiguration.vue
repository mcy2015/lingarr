<template>
    <!-- ═══ API Key 区卡(敏感值统一策略样板):挂载态只有布尔(已生成/
         未生成,无值)—— 加密设置批量读取已改回布尔,前端不再持有密文值。
         「显示密钥」为用户显式动作:点击调 GET /api/auth/apikey 取值(与铸钥
         同等守卫),本会话内可显示/复制/隐藏;生成响应直接带回值。值行默认
         掩码(前4+•••+后4,短值/未取值全掩码),掩码态 DOM 不含完整明文。
         用法折叠区 curl 恒占位符。 -->
    <div
        v-if="authEnabled == 'true'"
        class="border-line bg-secondary rounded-md border"
        data-test="apikey-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.apiKey.title') }}</h2>
            <span
                class="mono ml-auto rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                :class="
                    apiKeyConfigured
                        ? 'border-success/40 bg-success/10 text-success'
                        : 'border-line bg-tertiary text-secondary-content'
                "
                data-test="apikey-status">
                {{
                    apiKeyConfigured
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
                    :class="apiKeyConfigured ? 'text-success' : 'text-secondary-content'"
                    data-test="apikey-value">
                    {{ displayValue }}
                </code>
                <button
                    v-if="apiKeyConfigured"
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1.5 text-xs transition-colors"
                    :title="revealed ? t('settings.apiKey.hide') : t('settings.apiKey.show')"
                    :aria-label="revealed ? t('settings.apiKey.hide') : t('settings.apiKey.show')"
                    data-test="apikey-visibility"
                    @click="toggleReveal()">
                    <Icon :name="revealed ? 'eyeOn' : 'eyeOff'" class="h-4 w-4" />
                </button>
                <button
                    v-if="hasSessionValue"
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
                    v-if="apiKeyConfigured"
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
import { writeClipboard } from '@/cells/app/utils/clipboard'
import { useToastStore } from '@/cells/app/stores/toast'

const settingsStore = useSettingStore()
const { t } = useI18n()
const authApi = authService(axios)

const toastStore = useToastStore()
const loading = ref(false)
const revoking = ref(false)
const revealing = ref(false)

/// 挂载态 = 布尔(已生成/未生成,无值):加密设置批量读取只回 configured
/// 标志,密钥明文仅存于两个显式来源 —— 生成响应 / 显示密钥读取端点。
const revealed = ref(false)
const sessionKeyValue = ref<string | null>(null)
const usageOpen = ref(false)

const authEnabled = computed({
    get: () => settingsStore.getSetting(SETTINGS.AUTH_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.AUTH_ENABLED, newValue.toString(), true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const apiKeyConfigured = computed(() =>
    settingsStore.isEncryptedSettingConfigured(ENCRYPTED_SETTINGS.API_KEY)
)

/// 本会话已取到值(生成带回或显示时拉取)才可复制/纯显隐切换。
const hasSessionValue = computed(
    () => apiKeyConfigured.value && (sessionKeyValue.value ?? '') !== ''
)

/// 值行展示:未生成 → '—';已生成未取值 → 通用掩码(值不在前端);
/// 已取值 → 默认掩码(短值全掩码),显/隐仅切换渲染文本 —— 掩码态下完整
/// 明文不进入 DOM(值不在模板、不在属性,仅存于内存与剪贴板)。
const displayValue = computed(() => {
    if (!apiKeyConfigured.value) return '—'
    const value = sessionKeyValue.value
    if (value === null || value === '') return maskSecret('')
    return revealed.value ? value : maskSecret(value)
})

/// 显示密钥 = 用户显式动作(与铸钥同等暴露面):首次点击调
/// GET /api/auth/apikey 取值,之后本会话内纯显隐切换,不再请求。
const toggleReveal = async () => {
    if (!apiKeyConfigured.value || revealing.value) return
    if (sessionKeyValue.value === null) {
        revealing.value = true
        try {
            const response = await authApi.getApiKey()
            if ((response.apiKey ?? '') === '') {
                // 密钥已被其它会话吊销:静默回「未生成」。
                settingsStore.setEncryptedSettingState(ENCRYPTED_SETTINGS.API_KEY, false)
                return
            }
            sessionKeyValue.value = response.apiKey
            revealed.value = true
        } finally {
            revealing.value = false
        }
        return
    }
    revealed.value = !revealed.value
}

const copyApiKey = async () => {
    const value = sessionKeyValue.value ?? ''
    if (value === '') return
    try {
        await writeClipboard(value)
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
        // 生成响应直接带回值:本会话立即可显示/复制,无需再调读取端点。
        sessionKeyValue.value = response.apiKey
        settingsStore.setEncryptedSettingState(ENCRYPTED_SETTINGS.API_KEY, true)
    } finally {
        loading.value = false
    }
}

/// P2-4: 吊销 = 服务端清空加密 api_key(空串即失效)。本地只同步布尔状态
/// 回「未生成」并丢弃会话值 —— 不再经由设置通道写回。
const revokeApiKey = async () => {
    revoking.value = true
    try {
        revealed.value = false
        await authApi.revokeApiKey()
        sessionKeyValue.value = null
        settingsStore.setEncryptedSettingState(ENCRYPTED_SETTINGS.API_KEY, false)
        toastStore.push({ message: t('settings.apiKey.revoked'), type: 'success' })
    } finally {
        revoking.value = false
    }
}
</script>
