<template>
    <select
        class="border-line bg-primary text-primary-content focus:border-line focus:ring-accent block w-full rounded-md border px-2 py-1 text-sm focus:ring-2 md:w-auto"
        :value="modelValue.sortBy"
        @change="sortBy">
        <option
            v-for="(option, index) in options"
            :key="`${option.value}-${index}`"
            :value="option.value">
            {{ option.label }}
        </option>
    </select>
    <button
        class="ml-auto flex cursor-pointer items-center justify-end space-x-2 md:ml-0 md:justify-center"
        @click="orderBy">
        <span class="md:hidden">{{ t('common.orderBy') }}</span>
        <CaretUpIcon v-if="modelValue.isAscending" class="h-6 w-6" />
        <CaretDownIcon v-else class="h-6 w-6" />
    </button>
</template>

<script lang="ts" setup>
import { IOptions, IFilter } from '@/ts'
import { useI18n } from 'vue-i18n'
import CaretUpIcon from '@/components/icons/CaretUpIcon.vue'
import CaretDownIcon from '@/components/icons/CaretDownIcon.vue'

const { t } = useI18n()

const emit = defineEmits(['update:modelValue'])
const { modelValue, options } = defineProps<{
    modelValue: IFilter
    options: IOptions[]
}>()

const orderBy = () => {
    emit('update:modelValue', {
        ...modelValue,
        isAscending: !modelValue.isAscending
    })
}

const sortBy = (event: Event) => {
    emit('update:modelValue', {
        ...modelValue,
        sortBy: (event.target as HTMLSelectElement).value
    })
}
</script>
