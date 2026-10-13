<template>
    <div class="relative w-full md:w-96">
        <input
            :value="modelValue.searchQuery"
            type="text"
            :placeholder="t('common.searchMedia')"
            class="placeholder-primary/60 border-line bg-primary text-primary-content block w-full rounded-md border px-8 py-1 text-sm outline-hidden"
            @input="search" />
        <Icon
            name="search"
            class="text-secondary-content/60 absolute top-1/2 left-2 h-4 w-4 -translate-y-1/2 transform" />
        <Icon
            v-if="modelValue.searchQuery"
            name="times"
            class="text-secondary-content/60 absolute top-1/2 right-2 h-4 w-4 -translate-y-1/2 transform cursor-pointer"
            @click="clear" />
    </div>
</template>

<script lang="ts" setup>
import { IFilter } from '@/types'
import { useI18n } from 'vue-i18n'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

const { t } = useI18n()

const emit = defineEmits(['update:modelValue'])
const { modelValue } = defineProps<{
    modelValue: IFilter
}>()

const searchQuery = (value: string) => {
    emit('update:modelValue', {
        ...modelValue,
        searchQuery: value
    })
}

const search = (event: Event) => {
    searchQuery((event.target as HTMLInputElement).value)
}

const clear = () => {
    searchQuery('')
}
</script>
