<template>
    <div class="relative">
        <label v-if="label" class="mb-1 block text-sm">
            {{ label }}
        </label>
        <div
            ref="excludeClickOutside"
            class="border-accent flex cursor-pointer items-center justify-between rounded-md border"
            :class="size === 'sm' ? 'h-6 px-1.5 py-0.5 text-xs' : 'h-12 px-4 py-2'"
            @click="toggleDropdown">
            <span v-if="!selected" class="text-gray-400">
                {{ placeholder || t('common.selectItems') }}
            </span>
            <span v-else-if="size === 'sm'" class="truncate">{{ displaySelectedLabel }}</span>
            <div v-else class="flex max-h-12 flex-wrap gap-2 overflow-auto">
                <span
                    class="bg-accent flex cursor-pointer items-center rounded-md px-3 py-1 text-sm font-medium">
                    <span class="text-accent-content mr-2">{{ displaySelectedLabel }}</span>
                </span>
            </div>
            <div class="flex shrink-0 items-center">
                <LoaderCircleIcon v-if="isLoading" class="mr-2 h-4 w-4 animate-spin" />
                <CaretRightIcon
                    class="arrow-right transition-transform duration-200"
                    :class="[{ 'rotate-90': isOpen }, size === 'sm' ? 'h-3 w-3' : 'h-5 w-5']" />
            </div>
        </div>
        <ul
            v-show="isOpen"
            ref="clickOutside"
            class="border-accent bg-primary absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border shadow-lg">
            <li v-if="!sortedOptions.length" :class="size === 'sm' ? 'p-1.5 text-xs' : 'p-3'">
                {{ noOptions || t('common.selectSourceLanguageFirst') }}
            </li>
            <li
                v-for="(option, index) in sortedOptions"
                :key="`${option.value}-${index}`"
                class="cursor-pointer"
                :class="[
                    size === 'sm' ? 'px-2 py-1 text-xs' : 'px-4 py-2',
                    { 'bg-accent/20': isSelected(option.value) }
                ]"
                @click="selectOption(option)">
                {{ option.label }}
            </li>
        </ul>
    </div>
</template>

<script setup lang="ts">
import { Ref, ref, nextTick, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import CaretRightIcon from '@/components/icons/CaretRightIcon.vue'
import LoaderCircleIcon from '@/components/icons/LoaderCircleIcon.vue'
import useClickOutside from '@/composables/useClickOutside'
import { ISelectOption } from '@/ts'

const { t } = useI18n()

const props = withDefaults(
    defineProps<{
        label?: string
        options: ISelectOption[]
        selected?: string
        disabled?: boolean
        loadOnOpen?: boolean
        placeholder?: string
        noOptions?: string
        size?: 'sm' | 'md'
    }>(),
    {
        label: '',
        options: () => [],
        selected: '',
        placeholder: '',
        noOptions: '',
        selectedLabel: '',
        size: 'md'
    }
)

const emit = defineEmits(['update:selected', 'fetch-options'])

const isOpen: Ref<boolean> = ref(false)
const isLoading: Ref<boolean> = ref(false)
const clickOutside: Ref<HTMLElement | undefined> = ref()
const excludeClickOutside: Ref<HTMLElement | undefined> = ref()

const sortedOptions = computed(() => {
    return [...props.options].sort((a, b) => a.label.localeCompare(b.label))
})

const displaySelectedLabel = computed(() => {
    const option = props.options.find((item) => item.value === props.selected)
    return option ? option.label : props.selected
})

const toggleDropdown = async () => {
    if (props.disabled) return

    isOpen.value = !isOpen.value

    if (isOpen.value && props.loadOnOpen) {
        isLoading.value = true
        emit('fetch-options')
    }

    await nextTick()
}

const setLoadingState = async (loading: boolean) => {
    isLoading.value = loading
}

const selectOption = (option: ISelectOption) => {
    emit('update:selected', option.value)
    isOpen.value = false
}

const isSelected = (option: string) => {
    return props.selected == option
}

useClickOutside(
    clickOutside,
    () => {
        isOpen.value = false
    },
    excludeClickOutside
)

defineExpose({
    setLoadingState
})
</script>
