<template>
    <CardComponent :title="t('services.plugins.title')">
        <template #description>
            {{ t('services.plugins.descriptionPrefix') }}
            <a class="cursor-pointer underline" @click="router.push({ name: 'settings-providers' })">
                {{ t('services.plugins.servicesLink') }}
            </a>
            {{ t('services.plugins.descriptionSuffix') }}
        </template>
        <template #content>
            <div v-if="isLoading" class="text-sm opacity-60">
                {{ t('services.plugins.loading') }}
            </div>
            <div v-else-if="loadError" class="text-sm text-danger">{{ loadError }}</div>
            <div v-else-if="plugins.length === 0" class="text-sm opacity-60">
                {{ t('services.plugins.noPluginsPrefix') }}
                <CodeSnippet>PLUGINS_PATH</CodeSnippet>
                {{ t('services.plugins.noPluginsSuffix') }}
            </div>
            <ul v-else class="space-y-2">
                <li
                    v-for="plugin in plugins"
                    :key="plugin.provider"
                    class="border-line rounded-md border p-3">
                    <span class="font-semibold">{{ plugin.displayName }}</span>
                    <div v-if="plugin.description" class="mt-1 text-xs opacity-60">
                        {{ plugin.description }}
                    </div>
                    <div v-if="plugin.sourceFile" class="mt-1 text-xs opacity-60">
                        <CodeSnippet>{{ plugin.sourceFile }}</CodeSnippet>
                    </div>
                </li>
            </ul>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { IPluginSummary } from '@/types'
import services from '@/services'
import CardComponent from '@/components/common/CardComponent.vue'
import CodeSnippet from '@/components/common/CodeSnippet.vue'

const router = useRouter()
const { t } = useI18n()
const isLoading = ref(true)
const loadError = ref<string | null>(null)
const plugins = ref<IPluginSummary[]>([])

onMounted(async () => {
    try {
        const summaries = await services.plugin.list()
        plugins.value = summaries.filter((plugin) => !plugin.isBuiltIn)
    } catch (error) {
        console.error('Failed to load plugins', error)
        loadError.value = t('services.plugins.loadError')
    } finally {
        isLoading.value = false
    }
})
</script>
