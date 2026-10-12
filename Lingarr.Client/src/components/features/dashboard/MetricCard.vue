<template>
    <div class="rounded-md border border-line bg-secondary p-4">
        <h3 class="text-secondary-content/70 text-sm font-medium">
            {{ title }}
        </h3>
        <p class="text-primary-content mt-2 font-mono text-2xl font-bold tabular-nums">
            {{ formatNumber(value) }}
        </p>
    </div>
</template>

<script setup lang="ts">
interface Props {
    title: string
    value: number
}

withDefaults(defineProps<Props>(), {
    value: 0
})

const formatNumber = (num: number): string => {
    return new Intl.NumberFormat().format(num)
}
</script>
