<template>
    <CardComponent :title="t('services.webhook.title')">
        <template #description>
            {{ t('services.webhook.description') }}
        </template>
        <template #content>
            <div class="flex flex-col space-y-2">
                <span>
                    {{ t('services.webhook.goTo') }}
                    <b>Settings → Connect → Connections → +</b>
                    {{ t('services.webhook.andUseThisUrl') }}
                </span>
                <span class="font-semibold">Radarr</span>
                <CodeSnippet class="mt-1 block overflow-x-auto">{{ radarrWebhookUrl }}</CodeSnippet>
                <span class="font-semibold">Sonarr</span>
                <CodeSnippet class="mt-1 block overflow-x-auto">{{ sonarrWebhookUrl }}</CodeSnippet>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import CodeSnippet from '@/cells/kernel/components/CodeSnippet.vue'
import { resolveUrl } from '@/cells/app/utils/baseUrl'

const { t } = useI18n()

const radarrWebhookUrl = resolveUrl('/api/webhook/radarr')
const sonarrWebhookUrl = resolveUrl('/api/webhook/sonarr')
</script>
