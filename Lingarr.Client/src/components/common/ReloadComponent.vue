<template>
    <button
        type="button"
        class="focus-visible:ring-accent cursor-pointer rounded-md focus-visible:ring-2 focus-visible:outline-none"
        :aria-label="t('common.refresh')"
        :title="t('common.refresh')"
        @click="reload">
        <Icon
            name="reload"
            class="h-6 w-6 transition-transform duration-300 ease-in-out"
            :class="{ 'animate-spin': reloading }" />
    </button>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Icon from '@/components/icons/Icon.vue'

const emit = defineEmits(['toggle:update'])
const { t } = useI18n()
const reloading = ref(false)

function reload() {
    reloading.value = true
    emit('toggle:update', true)

    setTimeout(() => {
        reloading.value = false
    }, 1000)
}
</script>
