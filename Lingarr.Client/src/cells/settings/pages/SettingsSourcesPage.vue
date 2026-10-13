<template>
    <div class="space-y-3">
        <!-- 媒体源卡:Sonarr/Radarr 行 + Webhook 折叠 + 媒体源同步 cron + 路径映射行 -->
        <MediaSourcesCard class="px-4" />
        <!-- 自动化批次卡:每日补缺行(参数与开关解耦) -->
        <AutomationBatchesCard class="px-4" />
    </div>
</template>

<script setup lang="ts">
import MediaSourcesCard from '@/cells/settings/components/MediaSourcesCard.vue'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'
</script>
