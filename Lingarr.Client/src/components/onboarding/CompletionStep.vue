<template>
    <div class="space-y-6">
        <div>
            <h2 class="mb-4 text-xl font-semibold text-primary-content">
                {{ t('onboarding.completion.title') }}
            </h2>
        </div>

        <div class="rounded-lg border border-line bg-tertiary p-6">
            <h3 class="mb-4 text-lg font-semibold text-primary-content">
                {{ t('onboarding.completion.configuration') }}
            </h3>

            <div class="space-y-3">
                <div class="flex items-center justify-between">
                    <span class="text-sm text-secondary-content/60">
                        {{ t('onboarding.completion.authentication') }}
                    </span>
                    <span
                        class="rounded px-2 py-1 text-xs font-medium"
                        :class="
                            enableAuth === 'true'
                                ? 'bg-success/20 text-success'
                                : 'bg-secondary text-secondary-content/60'
                        ">
                        {{
                            enableAuth === 'true'
                                ? t('onboarding.completion.enabled')
                                : t('onboarding.completion.disabled')
                        }}
                    </span>
                </div>

                <div class="flex items-center justify-between">
                    <span class="text-sm text-secondary-content/60">
                        {{ t('onboarding.completion.telemetry') }}
                    </span>
                    <span
                        class="rounded px-2 py-1 text-xs font-medium"
                        :class="
                            enableTelemetry === 'true'
                                ? 'bg-success/20 text-success'
                                : 'bg-secondary text-secondary-content/60'
                        ">
                        {{
                            enableTelemetry === 'true'
                                ? t('onboarding.completion.enabled')
                                : t('onboarding.completion.disabled')
                        }}
                    </span>
                </div>
            </div>

            <div v-if="enableAuth === 'true'" class="mt-4 space-y-2 border-t border-line pt-4">
                <div class="flex items-center text-xs text-secondary-content/60">
                    <Icon name="exclamation" class="mr-2 h-4 w-4 text-info" />
                    {{ t('onboarding.completion.userAccountNote') }}
                </div>
                <div class="flex items-center text-xs text-secondary-content/60">
                    <Icon name="exclamation" class="mr-2 h-4 w-4 text-info" />
                    {{ t('onboarding.completion.apiKeyNote') }}
                </div>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import Icon from '@/components/icons/Icon.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

interface Props {
    enableAuth: string
    enableTelemetry: string
}

defineProps<Props>()
</script>
