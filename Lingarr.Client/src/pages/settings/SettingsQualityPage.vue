<template>
    <div class="space-y-3">
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.subtitle') }}
        </h2>
        <SubtitlePage />
        <GlossaryCard class="px-4" />
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import SubtitlePage from './SubtitlePage.vue'
import GlossaryCard from '@/components/features/settings/GlossaryCard.vue'

const { t } = useI18n()
</script>
