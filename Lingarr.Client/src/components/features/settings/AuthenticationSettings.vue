<template>
    <CardComponent :title="t('settings.authentication.title')">
        <template #description></template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <StatusMessage :message="error" type="error" />
                <div class="flex items-center space-x-2">
                    <span>{{ t('settings.authentication.enabled') }}</span>
                    <ToggleButton v-model="authEnabled">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                authEnabled === 'true'
                                    ? t('settings.status.enabled')
                                    : t('settings.status.disabled')
                            }}
                        </span>
                    </ToggleButton>
                </div>
                <div v-if="authEnabled === 'false'" class="text-sm text-secondary-content/60">
                    {{ t('settings.authentication.note') }}
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { SETTINGS } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import StatusMessage from '@/components/common/StatusMessage.vue'
import services from '@/services'

const settingsStore = useSettingStore()
const { t } = useI18n()
const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const error = ref<string>('')

const authEnabled = computed({
    get: () => settingsStore.getSetting(SETTINGS.AUTH_ENABLED) as string,
    set: async (newValue: string): Promise<void> => {
        if (newValue === 'true') {
            // auth enabled, check if users exist
            try {
                const hasUsers = await services.auth.hasAnyUsers()
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
        saveNotification.value?.show()
    }
})
</script>
