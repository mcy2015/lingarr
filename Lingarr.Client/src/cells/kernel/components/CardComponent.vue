<template>
    <div class="relative rounded-md border border-line bg-secondary p-6">
        <h2 v-if="title" class="mb-2 text-lg font-semibold text-primary-content">{{ title }}</h2>
        <p class="text-secondary-content/80 mb-6"><slot name="description"></slot></p>

        <div class="space-y-4">
            <slot name="content"></slot>
        </div>
    </div>
</template>

<script setup lang="ts">
// title is optional: drill-down pages render title-less stat containers
// (the pb-head carries the identity).
const { title } = defineProps<{
    title?: string
}>()
</script>
