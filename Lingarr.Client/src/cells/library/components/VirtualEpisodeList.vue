<template>
    <!-- ═══ Windowed table scroller (P2-8): renders only the viewport rows
         (± overscan) of a potentially thousand-episode season. Spacers keep
         the scrollbar honest; the scroll container itself is focusable so
         keyboard users can page through seasons without a mouse. ═══ -->
    <div
        ref="scrollEl"
        class="shell-scroll min-h-0 flex-1 overflow-auto"
        :tabindex="0"
        :role="label ? 'region' : undefined"
        :aria-label="label"
        data-test="virtual-scroll-container"
        @scroll.passive="onScroll">
        <table class="w-full text-[13px]">
            <slot name="header" />
            <tbody>
                <tr v-if="items.length === 0" data-test="virtual-empty-row">
                    <td :colspan="colspan" :class="cellClass">
                        <slot name="empty" />
                    </td>
                </tr>
                <template v-else>
                    <tr
                        v-if="padTop > 0"
                        :style="{ height: padTop + 'px' }"
                        aria-hidden="true"
                        data-test="virtual-spacer-top">
                        <td :colspan="colspan"></td>
                    </tr>
                    <template v-for="(item, i) in windowItems" :key="itemKey(item, start + i)">
                        <slot name="row" :item="item" :index="start + i" />
                    </template>
                    <tr
                        v-if="padBottom > 0"
                        :style="{ height: padBottom + 'px' }"
                        aria-hidden="true"
                        data-test="virtual-spacer-bottom">
                        <td :colspan="colspan"></td>
                    </tr>
                </template>
            </tbody>
        </table>
    </div>
</template>

<script setup lang="ts" generic="T">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'

/// Layout engines without a real viewport (happy-dom and friends) report
/// clientHeight 0 — fall back to a bounded default so those contexts render
/// a window instead of the whole list.
const FALLBACK_VIEWPORT_PX = 480

const props = withDefaults(
    defineProps<{
        /** Full item list — windowing is render-time only, never data-time. */
        items: readonly T[]
        /** Assumed uniform row height in px (spacer math; rows may grow). */
        itemHeight: number
        /** Extra rows rendered above/below the viewport. */
        overscan?: number
        /** Column count of the slotted table (spacer cells span it). */
        colspan?: number
        /** Accessible name for the scroll region; omit for no region role. */
        label?: string
        /** Stable key per item (falls back to the window-relative index). */
        keyOf?: (item: T, index: number) => string | number
    }>(),
    { overscan: 5, colspan: 1, label: undefined, keyOf: undefined }
)

defineSlots<{
    header: () => unknown
    row: (props: { item: T; index: number }) => unknown
    empty: () => unknown
}>()

const cellClass = 'border-line/50 align-middle border-b px-3 py-2'

const scrollEl = ref<HTMLElement | null>(null)
const scrollTop = ref(0)
const viewportPx = ref(FALLBACK_VIEWPORT_PX)

const measure = (): void => {
    const reported = scrollEl.value?.clientHeight ?? 0
    viewportPx.value = reported > 0 ? reported : FALLBACK_VIEWPORT_PX
}

const start = computed(() => {
    const first = Math.floor(scrollTop.value / props.itemHeight)
    return Math.min(props.items.length, Math.max(0, first - props.overscan))
})
const end = computed(() => {
    const first = Math.floor(scrollTop.value / props.itemHeight)
    const visible = Math.ceil(viewportPx.value / props.itemHeight)
    return Math.min(props.items.length, first + visible + props.overscan)
})
const windowItems = computed(() => props.items.slice(start.value, end.value))
const padTop = computed(() => start.value * props.itemHeight)
const padBottom = computed(() => Math.max(0, props.items.length - end.value) * props.itemHeight)

const onScroll = (): void => {
    scrollTop.value = scrollEl.value?.scrollTop ?? 0
}

const itemKey = (item: T, index: number): string | number =>
    props.keyOf ? props.keyOf(item, index) : index

// a new list reference (season switch) restarts at the top — stale deep
// scroll offsets must not leave the window past the new list's end
watch(
    () => props.items,
    () => {
        scrollTop.value = 0
        if (scrollEl.value) {
            scrollEl.value.scrollTop = 0
        }
    }
)

onMounted(() => {
    measure()
    window.addEventListener('resize', measure)
})
onUnmounted(() => window.removeEventListener('resize', measure))
</script>
