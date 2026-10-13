<template>
    <div class="grid grid-cols-1 items-stretch gap-3 px-4 lg:grid-cols-2 2xl:grid-cols-3">
        <!-- 字幕输出行式卡(原 SubtitleSettings + ValidationSettings 全量收纳) -->
        <SubtitleOutputCard />
        <GlossaryCard id="glossary" class="scroll-mt-24" />
        <LanguagePolicyCard />
    </div>
</template>

<script setup lang="ts">
import { onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import SubtitleOutputCard from '@/cells/settings/components/SubtitleOutputCard.vue'
import GlossaryCard from '@/cells/settings/components/GlossaryCard.vue'
import LanguagePolicyCard from '@/cells/settings/components/LanguagePolicyCard.vue'

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
