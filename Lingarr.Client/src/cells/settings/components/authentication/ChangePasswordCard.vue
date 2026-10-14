<template>
    <!-- ═══ 修改密码卡(P0-3):当前密码 + 新密码 + 确认 + 强度提示。成功后
         服务端吊销全部会话,ChangePasswordForm 统一回登录页。仅在认证开启
         时显示(关闭时无 Cookie 会话,端点本就 401)。 -->
    <div
        v-if="authEnabled == 'true'"
        class="border-line bg-secondary rounded-md border"
        data-test="change-password-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('changePassword.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('changePassword.cardNote') }}
            </span>
        </div>

        <div class="px-4 py-3">
            <ChangePasswordForm />
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import ChangePasswordForm from '@/cells/app/components/ChangePasswordForm.vue'

const { t } = useI18n()
const settingStore = useSettingStore()

const authEnabled = computed(
    () => (settingStore.getSetting(SETTINGS.AUTH_ENABLED) as string) ?? 'false'
)
</script>
