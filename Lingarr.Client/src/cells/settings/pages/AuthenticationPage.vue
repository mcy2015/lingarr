<template>
    <!-- ═══ 认证管理下钻屏(原型 screen-06 D 下钻):pb-head(← 系统 +
         启用状态 mono 副行)→ 认证开关行卡 + API Key 区卡(authEnabled 时)。
         数据流零变化:AUTH_ENABLED 设置 + /api/auth 探测 + API Key 加密设置。 -->
    <DrillDownPage
        scrollable
        :back-to="{ name: 'settings-system' }"
        :back-label="t('settings.menu.groups.system')"
        :title="t('settings.system.authRow')"
        :sub="subLine">
        <AuthenticationSettings />
        <ApiKeyConfiguration />
        <ChangePasswordCard />
    </DrillDownPage>
</template>
<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import AuthenticationSettings from '@/cells/settings/components/AuthenticationSettings.vue'
import ApiKeyConfiguration from '@/cells/settings/components/authentication/ApiKeyConfiguration.vue'
import ChangePasswordCard from '@/cells/settings/components/authentication/ChangePasswordCard.vue'

const { t } = useI18n()
const settingStore = useSettingStore()

/// mono 副行 = 真实启用状态(与系统卡认证行同文案)。
const authEnabled = computed(
    () => (settingStore.getSetting(SETTINGS.AUTH_ENABLED) as string) ?? 'false'
)
const subLine = computed(() =>
    authEnabled.value === 'true' ? t('settings.system.authOn') : t('settings.system.authOff')
)
</script>
