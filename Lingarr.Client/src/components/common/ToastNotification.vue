<template>
    <div
        class="pointer-events-none fixed top-4 right-4 z-50 flex w-full max-w-sm flex-col items-end gap-2">
        <transition-group
            enter-active-class="transition ease-out duration-300 transform"
            enter-from-class="translate-y-2 opacity-0 sm:translate-y-0 sm:translate-x-2"
            enter-to-class="translate-y-0 opacity-100 sm:translate-x-0"
            leave-active-class="transition ease-in duration-200"
            leave-from-class="opacity-100"
            leave-to-class="opacity-0">
            <div
                v-for="toast in toastStore.toasts"
                :key="toast.id"
                :role="toast.type === 'error' ? 'alert' : 'status'"
                class="border-line bg-secondary pointer-events-auto w-full overflow-hidden rounded-md shadow-lg">
                <div class="p-4">
                    <div class="flex items-start">
                        <div class="shrink-0">
                            <Icon
                                v-if="toast.type === 'success'"
                                name="checkMarkCircle"
                                class="h-6 w-6 text-success" />
                            <Icon
                                v-else-if="toast.type === 'error'"
                                name="timesCircle"
                                class="h-6 w-6 text-danger" />
                            <Icon v-else name="exclamation" class="h-6 w-6 text-info" />
                        </div>
                        <div class="ml-3 w-0 flex-1 pt-0.5">
                            <p class="text-sm font-medium text-primary-content">
                                {{ toast.message }}
                            </p>
                        </div>
                        <div class="ml-4 flex shrink-0">
                            <button
                                class="text-secondary-content/60 hover:text-primary-content inline-flex cursor-pointer rounded-md"
                                @click="toastStore.dismiss(toast.id)">
                                <span class="sr-only">{{ t('common.close') }}</span>
                                <Icon name="times" class="h-5 w-5" />
                            </button>
                        </div>
                    </div>
                </div>
                <div
                    class="toast-progress h-1"
                    :class="{
                        'bg-success': toast.type === 'success',
                        'bg-danger': toast.type === 'error',
                        'bg-info': toast.type === 'info'
                    }"></div>
            </div>
        </transition-group>
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import Icon from '@/components/icons/Icon.vue'
import { useToastStore } from '@/store/toast'

const toastStore = useToastStore()
const { t } = useI18n()
</script>

<style scoped>
/* Shrinking lifetime bar, matching the previous 5s auto-dismiss timer. */
.toast-progress {
    animation: toast-progress 5s linear forwards;
}

@keyframes toast-progress {
    from {
        width: 100%;
    }
    to {
        width: 0%;
    }
}
</style>
