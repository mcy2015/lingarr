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
            <tbody ref="tbodyEl">
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
import { computed, onMounted, onUnmounted, onUpdated, ref, watch } from 'vue'

/// Layout engines without a real viewport (happy-dom and friends) report
/// clientHeight 0 — fall back to a bounded default so those contexts render
/// a window instead of the whole list.
const FALLBACK_VIEWPORT_PX = 480

const props = withDefaults(
    defineProps<{
        /** Full item list — windowing is render-time only, never data-time. */
        items: readonly T[]
        /** Row height estimate in px — used until a real row is measured
         *  (C4: keeps spacer math honest when CSS drifts from the estimate). */
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
const tbodyEl = ref<HTMLElement | null>(null)
const scrollTop = ref(0)
const viewportPx = ref(FALLBACK_VIEWPORT_PX)
/// First rendered content row's offsetHeight once a real layout engine
/// reports one (happy-dom et al. report 0 → the prop estimate stays).
/// Spacers switch to the measured value so estimate drift never skews
/// the window (no jump rows while scrolling deep seasons).
const measuredItemHeight = ref(0)

/// Effective uniform row height: measurement wins over the estimate.
const itemHeightPx = computed(() => measuredItemHeight.value || props.itemHeight)

const measure = (): void => {
    const reported = scrollEl.value?.clientHeight ?? 0
    viewportPx.value = reported > 0 ? reported : FALLBACK_VIEWPORT_PX
}

/// Reads the first content row (spacers are aria-hidden and skipped; the
/// empty-state row never coexists with spacers). Zero heights — layout
/// engines without layout — never overwrite a previous measurement.
const measureRows = (): void => {
    if (!tbodyEl.value) return
    const row = tbodyEl.value.querySelector<HTMLTableRowElement>('tr:not([aria-hidden="true"])')
    const height = row?.offsetHeight ?? 0
    if (height > 0 && height !== measuredItemHeight.value) {
        measuredItemHeight.value = height
    }
}

const start = computed(() => {
    const first = Math.floor(scrollTop.value / itemHeightPx.value)
    return Math.min(props.items.length, Math.max(0, first - props.overscan))
})
const end = computed(() => {
    const first = Math.floor(scrollTop.value / itemHeightPx.value)
    const visible = Math.ceil(viewportPx.value / itemHeightPx.value)
    return Math.min(props.items.length, first + visible + props.overscan)
})
const windowItems = computed(() => props.items.slice(start.value, end.value))
const padTop = computed(() => start.value * itemHeightPx.value)
const padBottom = computed(
    () => Math.max(0, props.items.length - end.value) * itemHeightPx.value
)

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

// viewport + row heights are re-read together on resize (wrapping widths
// can change both); every reactive re-render (scroll, season switch, font
// swap) re-reads the live row height too — unchanged heights are guarded
// away, so this settles
const remeasure = (): void => {
    measure()
    measureRows()
}

onMounted(() => {
    measure()
    measureRows()
    window.addEventListener('resize', remeasure)
})
onUnmounted(() => window.removeEventListener('resize', remeasure))
onUpdated(measureRows)
</script>
