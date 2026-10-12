<template>
    <CardComponent :title="t('settings.telemetry.title')">
        <template #description>
            {{ t('settings.telemetry.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <SaveNotification ref="saveNotification" />
                <div class="flex items-center space-x-2">
                    <span>{{ t('settings.telemetry.shareStats') }}</span>

                    <ToggleButton v-model="telemetryEnabled">
                        <span class="text-primary-content text-sm font-medium">
                            {{
                                telemetryEnabled === 'true'
                                    ? t('settings.status.enabled')
                                    : t('settings.status.disabled')
                            }}
                        </span>
                    </ToggleButton>
                </div>

                <span class="text-sm">{{ t('settings.telemetry.previewLabel') }}</span>
                <div class="bg-primary rounded-md p-4">
                    <pre class="overflow-x-auto rounded-md p-4 text-xs">{{
                        JSON.stringify(previewData, null, 2)
                    }}</pre>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSettingStore } from '@/store/setting'
import { SETTINGS } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import services from '@/services'
import SaveNotification from '@/components/common/SaveNotification.vue'

const store = useSettingStore()
const { t } = useI18n()
const previewData = ref<any>(null)

const telemetryEnabled = computed({
    get: () => store.getSetting(SETTINGS.TELEMETRY_ENABLED) as string,
    set: (value: string) => store.updateSetting(SETTINGS.TELEMETRY_ENABLED, value, true)
})

onMounted(async () => {
    const data = await services.telemetry.preview()
    previewData.value = data
})
</script>
