<template>
    <div role="tablist" class="flex items-stretch" @keydown="onKeydown">
        <button
            v-for="option in options"
            :key="option.value"
            ref="tabRefs"
            type="button"
            role="tab"
            class="focus-visible:ring-accent cursor-pointer border-b-2 px-4 text-sm font-medium transition-colors duration-200 focus-visible:ring-2 focus-visible:outline-none"
            :class="
                option.value === modelValue
                    ? 'border-accent text-primary-content'
                    : 'text-secondary-content/60 hover:text-primary-content border-transparent'
            "
            :aria-selected="option.value === modelValue"
            :tabindex="option.value === modelValue ? 0 : -1"
            @click="emit('update:modelValue', option.value)">
            {{ option.label }}
        </button>
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ITabOption } from '@/types'

const props = defineProps<{
    modelValue: string
    options: ITabOption[]
}>()

const emit = defineEmits<{
    (e: 'update:modelValue', value: string): void
}>()

const tabRefs = ref<HTMLButtonElement[]>([])

/// Arrow/Home/End navigation with selection following focus (WAI-ARIA tabs
/// pattern, automatic activation).
const onKeydown = (event: KeyboardEvent) => {
    const currentIndex = tabRefs.value.findIndex((button) => button === document.activeElement)
    if (currentIndex === -1) return
    let nextIndex: number
    switch (event.key) {
        case 'ArrowRight':
        case 'ArrowDown':
            nextIndex = (currentIndex + 1) % tabRefs.value.length
            break
        case 'ArrowLeft':
        case 'ArrowUp':
            nextIndex = (currentIndex - 1 + tabRefs.value.length) % tabRefs.value.length
            break
        case 'Home':
            nextIndex = 0
            break
        case 'End':
            nextIndex = tabRefs.value.length - 1
            break
        default:
            return
    }
    event.preventDefault()
    const target = tabRefs.value[nextIndex]
    target?.focus()
    emit('update:modelValue', props.options[nextIndex].value)
}
</script>
