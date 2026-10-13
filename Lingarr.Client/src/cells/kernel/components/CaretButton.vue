<template>
    <button class="p-1">
        <Icon v-if="isExpanded" name="caretRight" class="h-4 w-4" />
        <Icon v-else name="caretDown" class="h-4 w-4" />
    </button>
</template>

<script setup lang="ts">
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { isExpanded } = defineProps<{
    isExpanded: boolean
}>()
</script>
