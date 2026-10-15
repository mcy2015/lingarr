<template>
    <div ref="clickOutside" class="relative z-10 inline-block text-left">
        <button
            type="button"
            class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent inline-flex h-8 w-8 cursor-pointer items-center justify-center rounded-md border transition-colors focus-visible:ring-2 focus-visible:outline-none"
            aria-haspopup="menu"
            :aria-expanded="isOpen"
            @click="toggleDropdown"
            @keydown.escape.stop="close">
            <span v-if="label" class="sr-only">{{ label }}</span>
            <slot name="button"></slot>
        </button>

        <transition
            enter-active-class="transition ease-out duration-100"
            enter-from-class="transform opacity-0 scale-95"
            enter-to-class="transform opacity-100 scale-100"
            leave-active-class="transition ease-in duration-75"
            leave-from-class="transform opacity-100 scale-100"
            leave-to-class="transform opacity-0 scale-95">
            <div
                v-if="isOpen"
                :class="getClasses()"
                class="absolute right-0 mt-2 origin-top-right rounded-md border border-line bg-secondary shadow-lg">
                <slot name="content"></slot>
            </div>
        </transition>
    </div>
</template>

<script setup lang="ts">
import { ref, Ref } from 'vue'
import useClickOutside from '@/cells/kernel/composables/useClickOutside'

const isOpen = ref(false)
const clickOutside: Ref<HTMLElement | undefined> = ref()
const { width = 'medium', label } = defineProps<{
    width: string
    label?: string
}>()

const getClasses = () => {
    switch (width) {
        case 'medium':
            return 'w-48'
        case 'large':
            return 'w-80 md:w-96'
        default:
            // wide enough for CJK locale labels ("简体中文") plus the check icon
            return 'w-32'
    }
}

const toggleDropdown = () => {
    isOpen.value = !isOpen.value
}

const close = () => {
    isOpen.value = false
}

defineExpose({ close })

useClickOutside(clickOutside, () => {
    isOpen.value = false
})
</script>
