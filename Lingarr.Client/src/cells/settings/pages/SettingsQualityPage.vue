<template>
    <div class="space-y-3">
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.subtitle') }}
        </h2>
        <SubtitlePage />
        <GlossaryCard id="glossary" class="px-4 scroll-mt-24" />
        <LanguagePolicyCard class="px-4" />
    </div>
</template>

<script setup lang="ts">
import { onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import SubtitlePage from './SubtitlePage.vue'
import GlossaryCard from '@/cells/settings/components/GlossaryCard.vue'
import LanguagePolicyCard from '@/cells/settings/components/LanguagePolicyCard.vue'

const { t } = useI18n()
const route = useRoute()

/// 校对台术语回流(P1-4)深链:settings quality + #glossary 定位术语表区。
const scrollToGlossary = (): void => {
    if (route.hash === '#glossary') {
        document.getElementById('glossary')?.scrollIntoView({ block: 'start' })
    }
}

onMounted(scrollToGlossary)
watch(() => route.hash, scrollToGlossary)
</script>
