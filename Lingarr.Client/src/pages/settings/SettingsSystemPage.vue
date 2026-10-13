<template>
    <div class="space-y-2">
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.authentication') }}
        </h2>
        <AuthenticationPage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.tasks') }}
        </h2>
        <SchedulePage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.logs') }}
        </h2>
        <LogsPage />
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AuthenticationPage from './AuthenticationPage.vue'
import SchedulePage from './SchedulePage.vue'
import LogsPage from './LogsPage.vue'

const { t } = useI18n()
</script>
