<template>
    <!-- ═══ 设置 · 媒体源(瘦身自原 sources tab):只留接线 —— Sonarr/Radarr 行、
         字幕源卡、Webhook 折叠、路径映射下钻。时间策略归自动化 tab。 -->
    <div class="grid grid-cols-1 items-stretch gap-3 px-4">
        <MediaSourcesCard />
        <SubtitleSourcesCard />
        <AudioIdCard />
    </div>
</template>

<script setup lang="ts">
import MediaSourcesCard from '@/cells/settings/components/MediaSourcesCard.vue'
import SubtitleSourcesCard from '@/cells/settings/components/SubtitleSourcesCard.vue'
import AudioIdCard from '@/cells/settings/components/AudioIdCard.vue'
</script>
