<template>
    <button
        :type="type"
        :disabled="disabled || loading"
        class="inline-flex cursor-pointer items-center justify-center rounded-md text-sm font-semibold transition-colors duration-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-primary disabled:cursor-not-allowed disabled:opacity-50"
        :class="[variantClasses, sizeClasses]"
        @click="handleClick">
        <Icon v-if="loading" name="loaderCircle" class="mr-2 h-4 w-4 animate-spin" />
        <slot></slot>
    </button>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import Icon from '@/components/icons/Icon.vue'

interface Props {
    variant?: 'primary' | 'secondary' | 'accent' | 'ghost'
    size?: 'xs' | 'sm' | 'md' | 'lg'
    type?: 'button' | 'submit' | 'reset'
    disabled?: boolean
    loading?: boolean
}

const props = withDefaults(defineProps<Props>(), {
    variant: 'primary',
    size: 'md',
    type: 'button',
    disabled: false,
    loading: false
})

const emit = defineEmits<{
    click: [event: MouseEvent]
}>()

const variantClasses = computed(() => {
    switch (props.variant) {
        case 'primary':
            return 'bg-accent text-accent-content border border-accent hover:bg-accent/90'
        case 'secondary':
            return 'bg-secondary text-secondary-content border border-line hover:border-accent/60 hover:text-primary-content'
        case 'accent':
            return 'bg-transparent text-accent border-2 border-accent hover:bg-accent/10'
        case 'ghost':
            return 'bg-transparent text-secondary-content border border-transparent hover:bg-tertiary hover:text-primary-content'
        default:
            return 'bg-accent text-accent-content border border-accent hover:bg-accent/90'
    }
})

const sizeClasses = computed(() => {
    switch (props.size) {
        case 'xs':
            return 'px-3 py-1 text-xs'
        case 'sm':
            return 'px-4 py-1.5 text-xs'
        case 'md':
            return 'min-h-10 px-5 py-2 text-sm'
        case 'lg':
            return 'min-h-11 px-6 py-2.5 text-base'
        default:
            return 'px-6 py-2 text-sm'
    }
})

const handleClick = (event: MouseEvent) => {
    if (!props.disabled && !props.loading) {
        emit('click', event)
    }
}
</script>
