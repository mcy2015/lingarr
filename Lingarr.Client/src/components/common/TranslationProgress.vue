<template>
    <div class="relative grow select-none" :title="`${clamped}%`">
        <!-- NLE-style track: segmented fill + playhead -->
        <div class="h-2 w-full select-none overflow-hidden rounded-sm bg-tertiary">
            <div
                class="h-full select-none rounded-sm transition-all duration-1000 ease-out"
                :style="{
                    width: `${fillWidth}%`,
                    background: fillBackground
                }"></div>
        </div>
        <div
            v-if="showPlayhead"
            class="pointer-events-none absolute inset-y-0 w-0.5 rounded-full"
            :style="{ left: `${clamped}%`, background: fillColor }"></div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
    defineProps<{
        progress: number
        /** When omitted, renders the classic in-progress look (accent + playhead). */
        status?: string
    }>(),
    {
        status: undefined
    }
)

const clamped = computed(() => Math.max(0, Math.min(100, props.progress)))

const fillColor = computed(() => {
    switch (props.status) {
        case 'Completed':
            return 'var(--success)'
        case 'Failed':
            return 'var(--danger)'
        default:
            return 'var(--accent)'
    }
})

// completed requests always show a full solid bar; everything else uses the
// last known progress (failed requests keep the bar where they died)
const fillWidth = computed(() => (props.status === 'Completed' ? 100 : clamped.value))

const fillBackground = computed(() =>
    props.status === 'Completed' || props.status === 'Failed'
        ? fillColor.value
        : 'repeating-linear-gradient(90deg, var(--accent) 0 6px, transparent 6px 9px)'
)

const showPlayhead = computed(
    () => props.status !== 'Completed' && clamped.value > 0 && clamped.value < 100
)
</script>
