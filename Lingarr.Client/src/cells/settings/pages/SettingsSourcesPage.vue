<template>
    <div class="grid grid-cols-1 items-stretch gap-3 px-4 lg:grid-cols-2">
        <!-- 媒体源卡:Sonarr/Radarr 行 + Webhook 折叠 + 媒体源同步 cron + 路径映射行 -->
        <MediaSourcesCard />
        <!-- 自动化批次卡:每日补缺行(参数与开关解耦) -->
        <AutomationBatchesCard />
    </div>
</template>

<script setup lang="ts">
import MediaSourcesCard from '@/cells/settings/components/MediaSourcesCard.vue'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'
</script>
