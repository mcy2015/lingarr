<template>
    <div class="relative rounded-md border border-line bg-secondary p-6">
        <h2 v-if="title" class="mb-2 text-lg font-semibold text-primary-content">{{ title }}</h2>
        <!-- walk-through fix D5: an empty description slot must not hold the
             mb-6 gutter open — the paragraph only renders with real content. -->
        <p v-if="hasDescription" class="text-secondary-content/80 mb-6"><slot name="description"></slot></p>

        <div class="space-y-4">
            <slot name="content"></slot>
        </div>
    </div>
</template>

<script setup lang="ts">
import { Comment, computed, isVNode, useSlots } from 'vue'

// title is optional: drill-down pages render title-less stat containers
// (the pb-head carries the identity).
const { title } = defineProps<{
    title?: string
}>()

const slots = useSlots()

/// A description node counts as content unless it is a comment or
/// whitespace-only text; elements/components are assumed to render something.
const nodeHasContent = (node: unknown): boolean => {
    if (!isVNode(node)) {
        return String(node ?? '').trim().length > 0
    }
    if (node.type === Comment) {
        return false
    }
    if (typeof node.children === 'string') {
        return node.children.trim().length > 0
    }
    if (Array.isArray(node.children)) {
        return node.children.some(nodeHasContent)
    }
    return true
}

const hasDescription = computed(() => (slots.description?.() ?? []).some(nodeHasContent))
</script>
