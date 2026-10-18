<template>
    <!-- ═══ 下钻屏统一页头(原型 .pb-head,global-interactions):返回按钮 +
         标题块(b 15px + mono 11.5px 副行)+ flex 间隔 + 右侧动作组(1-3 个,
         含 primary)。动作通过 #actions 插槽注入,primary 语义由调用方用
         ButtonComponent variant 表达。 -->
    <div class="flex flex-wrap items-center gap-x-4 gap-y-2.5">
        <button
            v-if="backTo && backLabel"
            type="button"
            class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent inline-flex shrink-0 cursor-pointer items-center gap-1 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
            data-test="pb-back"
            @click="goBack">
            <Icon name="arrowLeft" class="h-3.5 w-3.5" aria-hidden="true" />
            {{ backLabel }}
        </button>
        <div class="min-w-0">
            <b class="text-primary-content block truncate text-[15px] font-semibold">
                {{ title }}
            </b>
            <div v-if="sub" class="mono text-secondary-content truncate text-[11.5px]">
                {{ sub }}
            </div>
        </div>
        <span class="flex-1"></span>
        <div class="flex flex-wrap items-center gap-2">
            <slot name="actions"></slot>
        </div>
    </div>
</template>

<script setup lang="ts">
import { type RouteLocationRaw, useRouter } from 'vue-router'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import { previousRoute } from '@/cells/app/router'

const props = defineProps<{
    /** Route target of the「← 来源屏」back button; omit to hide it. */
    backTo?: RouteLocationRaw
    /** Visible label of the back button (e.g. "Queue" in "← Queue"). */
    backLabel?: string
    title: string
    /** Mono 11.5px context line under the title; omit to hide. */
    sub?: string
}>()

const router = useRouter()

// A plain push to backTo drops the caller's query — coming back from a
// drill-down then reset the source screen to its defaults (library page 1,
// no filters) instead of the state the user left. When the previous history
// entry IS the backTo route, history back restores that entry's full URL
// (query included) and its scroll position; a deep-linked entry (no
// qualifying history) still pushes the target.
const goBack = (): void => {
    const previous = previousRoute.value
    if (previous?.name && previous.name === router.resolve(props.backTo!).name) {
        router.back()
        return
    }
    router.push(props.backTo!)
}
</script>
