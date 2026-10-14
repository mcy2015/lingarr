<template>
    <!-- ═══ 下钻页统一骨架(global-interactions):pb-head + 统一内容区容器。
         容器规范以 schedule 页(用户认可的 B 形态)量取:与主导航屏一致的
         内容边距 p-4 lg:p-6、页头下间距与区块间 gap-3.5、根级 min-h-0 防
         溢出。默认插槽承接各页内容(统计卡行/表格/卡片流保持各自形态,
         卡头 13px 标题体系由各页卡片自带);#actions 透传给 pb-head。
         scrollable 供纵向卡片流页(authentication)整列滚动。 -->
    <div
        class="w-full flex h-full min-h-0 flex-col gap-3.5 p-4 lg:p-6"
        :class="scrollable ? 'shell-scroll overflow-y-auto' : 'overflow-hidden'"
        data-test="dd-page">
        <PbHead :back-to="backTo" :back-label="backLabel" :title="title" :sub="sub">
            <template #actions>
                <slot name="actions"></slot>
            </template>
        </PbHead>
        <div class="flex min-h-0 flex-1 flex-col gap-3.5" data-test="dd-content">
            <slot></slot>
        </div>
    </div>
</template>

<script setup lang="ts">
import { type RouteLocationRaw } from 'vue-router'
import PbHead from '@/cells/app/components/PbHead.vue'

defineProps<{
    /** Route target of the「← 来源屏」back button; omit to hide it. */
    backTo?: RouteLocationRaw
    /** Visible label of the back button (e.g. "Queue" in "← Queue"). */
    backLabel?: string
    title: string
    /** Mono 11.5px context line under the title; omit to hide. */
    sub?: string
    /** Longitudinal card-flow pages scroll the whole column (shell-scroll +
         overflow-y-auto) instead of clipping it inside the viewport. */
    scrollable?: boolean
}>()
</script>
