<template>
    <div class="flex flex-col space-y-3">
        <div
            v-if="manifest.description"
            class="text-secondary-content/80 text-xs"
            v-html="manifest.description" />
        <div
            v-if="manifest.settings.length === 0 && !manifest.description"
            class="text-sm opacity-60">
            {{ t('services.plugins.noSettings') }}
        </div>

        <PluginField
            v-for="field in manifest.settings"
            :key="field.key"
            :field="field"
            @save="emit('save')" />
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { IPluginManifest } from '@/ts'
import PluginField from '@/components/features/settings/PluginField.vue'

const { t } = useI18n()

defineProps<{
    manifest: IPluginManifest
}>()

const emit = defineEmits(['save'])
</script>
