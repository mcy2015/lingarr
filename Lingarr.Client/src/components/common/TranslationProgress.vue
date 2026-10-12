<template>
    <div class="relative grow select-none" :title="`${clamped}%`">
        <!-- NLE-style track: segmented fill + playhead -->
        <div class="h-2 w-full select-none overflow-hidden rounded-sm bg-tertiary">
            <div
                class="h-full select-none rounded-sm transition-all duration-1000 ease-out"
                :style="{
                    width: `${clamped}%`,
                    background:
                        'repeating-linear-gradient(90deg, var(--accent) 0 6px, transparent 6px 9px)'
                }"></div>
        </div>
        <div
            class="pointer-events-none absolute inset-y-0 w-0.5 rounded-full bg-accent"
            :style="{ left: `${clamped}%` }"></div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
    progress: number
}>()

const clamped = computed(() => Math.max(0, Math.min(100, props.progress)))
</script>
