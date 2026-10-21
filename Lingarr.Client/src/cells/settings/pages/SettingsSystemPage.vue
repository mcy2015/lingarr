<template>
    <!-- ═══ 设置 · 系统(原型 screen-06 D):单张行式卡 —— 认证行(API Key
         chip 派生 + 管理下钻)、日志下钻行,卡头 meta 为实例 store 真实版本。
         定时任务行归自动化 tab(IA 重构批 2)。 -->
    <div class="space-y-2">
        <div class="border-line bg-secondary mx-4 rounded-md border" data-test="system-card">
            <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('settings.system.title') }}</h2>
                <span
                    v-if="version"
                    class="text-secondary-content mono ml-auto text-[11px]"
                    data-test="system-version">
                    {{ version }}
                </span>
            </div>

            <!-- 认证行:状态 + API Key chip(由发放状态派生)+ 管理下钻 -->
            <div
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
                data-test="system-row-auth">
                <span class="dot flex-none" :class="authEnabled === 'true' ? 'bg-success' : 'bg-line'"></span>
                <b class="flex-none">{{ t('settings.system.authRow') }}</b>
                <span class="text-secondary-content min-w-0 flex-1 truncate text-xs">
                    {{ authEnabled === 'true' ? t('settings.system.authOn') : t('settings.system.authOff') }}
                </span>
                <span class="ml-auto flex flex-none items-center gap-2">
                    <span
                        v-if="apiKeyIssued"
                        class="mono text-success-content rounded-[3px] border border-success/40 bg-success/10 px-1.5 py-px text-[10.5px]"
                        data-test="apikey-chip">
                        {{ t('settings.system.apiKeyIssued') }}
                    </span>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                        data-test="auth-manage"
                        @click="router.push({ name: 'settings-authentication' })">
                        {{ t('settings.system.manage') }}
                    </button>
                </span>
            </div>

            <!-- 日志行 -->
            <div
                class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
                data-test="system-row-logs">
                <span class="dot bg-success flex-none"></span>
                <b class="flex-none">{{ t('settings.logs.heading') }}</b>
                <span class="text-secondary-content min-w-0 flex-1 truncate text-xs">
                    {{ t('settings.logs.topbarSub') }}
                </span>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                    data-test="system-open-logs"
                    @click="router.push({ name: 'logs' })">
                    {{ t('settings.logs.open') }}
                </button>
            </div>
        </div>

        <!-- 通知卡(B11):webhook + Telegram 关键事件外发 -->
        <NotificationsCard />
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ENCRYPTED_SETTINGS, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useInstanceStore } from '@/cells/app/stores/instance'
import NotificationsCard from '@/cells/settings/components/NotificationsCard.vue'

const { t } = useI18n()
const router = useRouter()
const settingStore = useSettingStore()
const instanceStore = useInstanceStore()

const authEnabled = computed(
    () => (settingStore.getSetting(SETTINGS.AUTH_ENABLED) as string) ?? 'false'
)

/// API Key chip:由发放状态派生(批量加密读取只回「已配置」布尔)
const apiKeyIssued = computed(() =>
    settingStore.isEncryptedSettingConfigured(ENCRYPTED_SETTINGS.API_KEY)
)

/// 版本 meta:实例 store 的真实版本
const version = computed(() => instanceStore.getVersion.currentVersion || '')
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
