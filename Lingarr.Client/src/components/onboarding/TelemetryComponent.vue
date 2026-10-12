<template>
    <div class="space-y-6">
        <div>
            <h2 class="mb-4 text-xl font-semibold text-primary-content">
                {{ t('onboarding.telemetry.title') }}
            </h2>
            <p class="mb-6 text-sm text-secondary-content/60">
                {{ t('onboarding.telemetry.description') }}
            </p>
        </div>

        <StatusMessage type="success" :message="t('onboarding.telemetry.privacyNote')" />

        <div class="flex flex-col space-y-4">
            <span class="text-sm">{{ t('onboarding.telemetry.exampleLabel') }}</span>
            <div class="bg-primary rounded-md p-4">
                <pre class="overflow-x-auto rounded-md p-4 text-xs">{{
                    JSON.stringify(previewData, null, 2)
                }}</pre>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusMessage from '@/components/common/StatusMessage.vue'

const { t } = useI18n()

const previewData = ref({
    version: '1.0.3',
    reportDate: '2025-12-15',
    platform: 'linux/x64',
    metrics: {
        filesTranslated: 5,
        linesTranslated: 3773,
        charactersTranslated: 77225,
        serviceUsage: {
            localai: 5
        },
        languagePairs: {
            'en:nl': 5
        },
        mediaTypeUsage: {
            Episode: 5
        },
        modelUsage: {
            'localai:openai/gpt-4o': 5
        }
    }
})
</script>
