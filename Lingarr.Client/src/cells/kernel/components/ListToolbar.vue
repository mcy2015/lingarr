<template>
    <div class="bg-tertiary flex flex-wrap items-center justify-between gap-2 p-4">
        <SearchComponent v-model="filterModel" />
        <div
            class="flex w-full flex-col gap-2 md:w-fit md:flex-row md:justify-between md:space-x-2"
            :class="{ 'md:items-center': centerActions }">
            <slot name="actions" />
            <div class="flex flex-wrap items-center gap-2">
                <SortControls v-model="filterModel" :options="sortOptions" />
                <ReloadComponent @toggle:update="emit('reload')" />
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { IFilter, IOptions } from '@/types'
import SearchComponent from '@/cells/kernel/components/SearchComponent.vue'
import SortControls from '@/cells/kernel/components/SortControls.vue'
import ReloadComponent from '@/cells/kernel/components/ReloadComponent.vue'

const { filter, sortOptions, centerActions = false } = defineProps<{
    /** Page-owned filter state; writes are re-emitted via `update:filter`. */
    filter: IFilter
    /** Sort options for the SortControls select. */
    sortOptions: IOptions[]
    /** Vertically center the action row on md+ (movie/show pages do, translations does not). */
    centerActions?: boolean
}>()

const emit = defineEmits<{
    (e: 'update:filter', value: IFilter): void
    (e: 'reload'): void
}>()

const filterModel = computed({
    get: () => filter,
    set: (value: IFilter) => emit('update:filter', value)
})
</script>
