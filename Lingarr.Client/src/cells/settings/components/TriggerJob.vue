<template>
    <button
        type="button"
        class="focus-visible:ring-accent cursor-pointer rounded-md focus-visible:ring-2 focus-visible:outline-none"
        :aria-label="t('common.runNow')"
        :title="t('common.runNow')"
        @click="trigger">
        <Icon v-if="loading" name="loaderCircle" class="h-5 w-5 animate-spin" />
        <Icon v-else name="start" class="h-5 w-5" />
    </button>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import { delay } from '@/utils/delay'

const emit = defineEmits(['toggle:trigger'])
const loading = ref(false)
const { t } = useI18n()

const trigger = async () => {
    if (loading.value) return
    loading.value = true
    emit('toggle:trigger')
    await delay(1000)
    loading.value = false
}
</script>
