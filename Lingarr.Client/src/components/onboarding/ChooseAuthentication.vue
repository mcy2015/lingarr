<template>
    <div class="space-y-6">
        <div>
            <h2 class="mb-4 text-xl font-semibold text-primary-content">
                {{ t('onboarding.choose.title') }}
            </h2>
            <p class="mb-6 text-sm text-secondary-content/60">
                {{ t('onboarding.choose.description') }}
            </p>
        </div>

        <div
            class="rounded-lg border-2 p-6 transition-colors"
            :class="
                enableAuth == 'true'
                    ? 'border-line bg-tertiary'
                    : 'bg-primary/10 border-primary'
            ">
            <div class="flex items-start">
                <div class="ml-4 flex-1">
                    <h3 class="text-lg font-semibold text-primary-content">
                        {{ t('onboarding.choose.enableAuthTitle') }}
                    </h3>
                    <p class="mt-1 text-sm text-secondary-content/60">
                        {{ t('onboarding.choose.enableAuthDescription') }}
                    </p>
                </div>
                <ToggleButton v-model="enableAuth" />
            </div>
        </div>

        <div class="rounded-md border border-warning/40 bg-warning/15 p-4">
            <p class="text-sm text-warning">
                {{ t('onboarding.choose.warning') }}
            </p>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useOnboardingStore } from '@/store/onboarding'
import ToggleButton from '@/components/common/ToggleButton.vue'

const { t } = useI18n()
const onboardingStore = useOnboardingStore()

const enableAuth = computed({
    get: (): string => onboardingStore.getEnableAuth,
    set: (newValue: string): void => {
        onboardingStore.setEnableAuth(newValue)
    }
})
</script>
