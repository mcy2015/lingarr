<template>
    <div
        class="grid grid-flow-row auto-rows-max grid-cols-1 gap-4 p-4 md:grid-cols-2 xl:grid-cols-2 2xl:grid-cols-3">
        <ServicesSettings />
        <TranslationSettings />
    </div>
</template>

<script setup lang="ts">
import ServicesSettings from '@/cells/settings/components/ServicesSettings.vue'
import TranslationSettings from '@/cells/settings/components/TranslationSettings.vue'
</script>
