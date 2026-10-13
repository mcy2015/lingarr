<template>
    <div class="space-y-2">
        <!-- The integrations section is headed by the IntegrationSettings
             card itself ("Integrations") — no extra section heading here. -->
        <IntegrationPage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.automation') }}
        </h2>
        <AutomationPage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.mapping') }}
        </h2>
        <MappingPage />
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import IntegrationPage from './IntegrationPage.vue'
import AutomationPage from './AutomationPage.vue'
import MappingPage from './MappingPage.vue'

const { t } = useI18n()
</script>
