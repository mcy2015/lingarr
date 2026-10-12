<template>
    <div v-if="message" class="rounded-md border p-4" :class="[borderClass, bgClass]">
        <div class="flex items-start">
            <div v-if="showIcon" class="shrink-0">
                <Icon v-if="type === 'success'" name="checkMarkCircle" class="h-5 w-5" :class="iconClass" />
                <Icon v-else-if="type === 'error'" name="timesCircle" class="h-5 w-5" :class="iconClass" />
                <Icon v-else name="exclamation" class="h-5 w-5" :class="iconClass" />
            </div>
            <div :class="{ 'ml-3': showIcon }">
                <p class="text-sm" :class="textClass">{{ message }}</p>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import Icon from '@/components/icons/Icon.vue'

type MessageType = 'success' | 'error' | 'warning' | 'info'

const props = withDefaults(
    defineProps<{
        message?: string
        type?: MessageType
        showIcon?: boolean
    }>(),
    {
        type: 'info',
        showIcon: false
    }
)

const borderClass = computed(() => {
    switch (props.type) {
        case 'success':
            return 'border-success/40'
        case 'error':
            return 'border-danger/40'
        case 'warning':
            return 'border-warning/40'
        case 'info':
            return 'border-info/40'
        default:
            return 'border-line'
    }
})

const bgClass = computed(() => {
    switch (props.type) {
        case 'success':
            return 'bg-success/15'
        case 'error':
            return 'bg-danger/15'
        case 'warning':
            return 'bg-warning/15'
        case 'info':
            return 'bg-info/15'
        default:
            return 'bg-tertiary/50'
    }
})

const textClass = computed(() => {
    switch (props.type) {
        case 'success':
            return 'text-success'
        case 'error':
            return 'text-danger'
        case 'warning':
            return 'text-warning'
        case 'info':
            return 'text-info'
        default:
            return 'text-secondary-content/60'
    }
})

const iconClass = computed(() => {
    switch (props.type) {
        case 'success':
            return 'text-success'
        case 'error':
            return 'text-danger'
        case 'warning':
            return 'text-warning'
        case 'info':
            return 'text-info'
        default:
            return 'text-secondary-content/60'
    }
})
</script>
