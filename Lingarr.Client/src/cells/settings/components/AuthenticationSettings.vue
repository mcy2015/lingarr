<template>
    <!-- ═══ 认证开关卡(原型 screen-06 setlist 行式):状态行(名称 + 提示 +
         switch)+ 错误信息。启用前经 /api/auth 校验存在用户(原逻辑保留)。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="auth-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.authentication.title') }}</h2>
        </div>

        <div class="border-line px-4 py-2.5">
            <StatusMessage :message="error" type="error" />
            <div
                class="flex flex-wrap items-center gap-x-3 gap-y-2 text-[13px]"
                data-test="auth-row-enabled">
                <b class="min-w-0 flex-1 font-semibold">
                    {{ t('settings.authentication.enabled') }}
                    <span
                        v-if="authEnabled === 'false'"
                        class="text-secondary-content mt-0.5 block text-xs font-normal">
                        {{ t('settings.authentication.note') }}
                    </span>
                </b>
                <ToggleButton v-model="authEnabled">
                    <span class="text-primary-content text-xs font-medium">
                        {{
                            authEnabled === 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { useSettingStore } from '@/cells/app/stores/setting'
import { SETTINGS } from '@/types'
import { useToastStore } from '@/cells/app/stores/toast'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import StatusMessage from '@/cells/kernel/components/StatusMessage.vue'
import { authService } from '@/cells/app/api/authService'

const settingsStore = useSettingStore()
const { t } = useI18n()
const toastStore = useToastStore()
const authApi = authService(axios)
const error = ref<string>('')

const authEnabled = computed({
    get: () => settingsStore.getSetting(SETTINGS.AUTH_ENABLED) as string,
    set: async (newValue: string): Promise<void> => {
        if (newValue === 'true') {
            // auth enabled, check if users exist
            try {
                const hasUsers = await authApi.hasAnyUsers()
                if (!hasUsers) {
                    error.value = t('settings.authentication.noUsers')
                    return
                }
            } catch (err: any) {
                console.error('Error checking for users:', err)
                error.value = t('settings.authentication.verifyFailed')
                return
            }
            setTimeout(() => {
                error.value = ''
            }, 5000)
        }

        error.value = ''
        settingsStore.updateSetting(SETTINGS.AUTH_ENABLED, newValue.toString(), true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})
</script>
