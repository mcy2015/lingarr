<template>
    <div class="cursor-pointer" @click="trigger">
        <Icon v-if="loading" name="loaderCircle" class="h-5 w-5 animate-spin" />
        <Icon v-else name="start" class="h-5 w-5" />
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import Icon from '@/components/icons/Icon.vue'
import { delay } from '@/utils/delay'

const emit = defineEmits(['toggle:trigger'])
const loading = ref(false)

const trigger = async () => {
    if (loading.value) return
    loading.value = true
    emit('toggle:trigger')
    await delay(1000)
    loading.value = false
}
</script>
