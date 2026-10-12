<template>
    <div class="space-y-2">
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.services') }}
        </h2>
        <ServicesPage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.plugins') }}
        </h2>
        <PluginsPage />
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import ServicesPage from './ServicesPage.vue'
import PluginsPage from './PluginsPage.vue'

const { t } = useI18n()
</script>
