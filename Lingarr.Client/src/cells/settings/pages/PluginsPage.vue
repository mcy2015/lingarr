<template>
    <!-- ═══ 外部插件下钻屏(原型 screen-06 A 下钻):pb-head(← 翻译服务 +
         已加载数 mono 副行)→ 插件行式卡(名称 + 描述 + mono 源文件)。
         数据流沿用原 PluginsSettings:/api/plugin 列表过滤非内置项。 -->
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <PbHead
            :back-to="{ name: 'settings-providers' }"
            :back-label="t('settings.menu.groups.providers')"
            :title="t('settings.menu.plugins')"
            :sub="subLine" />

        <div
            class="border-line bg-secondary shell-scroll min-h-0 flex-1 overflow-y-auto rounded-md border"
            data-test="plugins-card">
            <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('services.plugins.title') }}</h2>
                <span
                    v-if="!isLoading && !loadError"
                    class="text-secondary-content mono ml-auto text-[11px]">
                    {{ subLine }}
                </span>
            </div>

            <!-- 说明行:凭据入口回 providers(原有链接保留) -->
            <p class="text-secondary-content border-line border-b px-4 py-2.5 text-[12px] leading-snug">
                {{ t('services.plugins.descriptionPrefix') }}
                <a
                    class="text-info cursor-pointer underline"
                    data-test="plugins-services-link"
                    @click="router.push({ name: 'settings-providers' })">
                    {{ t('services.plugins.servicesLink') }}
                </a>
                {{ t('services.plugins.descriptionSuffix') }}
            </p>

            <div v-if="isLoading" class="text-secondary-content px-4 py-5 text-[13px]">
                {{ t('services.plugins.loading') }}
            </div>
            <div v-else-if="loadError" class="text-danger px-4 py-5 text-[13px]" data-test="plugins-error">
                {{ loadError }}
            </div>
            <div
                v-else-if="plugins.length === 0"
                class="text-secondary-content px-4 py-5 text-[13px]">
                {{ t('services.plugins.noPluginsPrefix') }}
                <CodeSnippet>PLUGINS_PATH</CodeSnippet>
                {{ t('services.plugins.noPluginsSuffix') }}
            </div>

            <!-- 插件行:setlist 形态(dot + 名称 + 描述 hint + mono 源文件) -->
            <div
                v-for="plugin in plugins"
                v-else
                :key="plugin.provider"
                class="border-line flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b px-4 py-2.5 text-[13px] last:border-b-0"
                :data-test="`plugin-row-${plugin.provider}`">
                <span class="dot bg-success flex-none"></span>
                <b class="flex-none font-semibold">{{ plugin.displayName }}</b>
                <span v-if="plugin.description" class="text-secondary-content min-w-0 flex-1 truncate text-xs" :title="plugin.description">
                    {{ plugin.description }}
                </span>
                <span v-else class="flex-1"></span>
                <span
                    v-if="plugin.sourceFile"
                    class="mono text-secondary-content min-w-0 flex-none truncate text-xs"
                    :title="plugin.sourceFile">
                    {{ plugin.sourceFile }}
                </span>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import { IPluginSummary } from '@/types'
import { pluginService } from '@/cells/settings/services/pluginService'
import PbHead from '@/cells/app/components/PbHead.vue'
import CodeSnippet from '@/cells/kernel/components/CodeSnippet.vue'

const pluginApi = pluginService(axios)

const router = useRouter()
const { t } = useI18n()
const isLoading = ref(true)
const loadError = ref<string | null>(null)
const plugins = ref<IPluginSummary[]>([])

/// mono 副行只用真实数据:加载前仅基线文案,加载后附插件数。
const subLine = computed(() => {
    if (isLoading.value || plugins.value.length === 0) {
        return t('settings.plugins.subLineBase')
    }
    return t('settings.plugins.subLine', { count: plugins.value.length })
})

onMounted(async () => {
    try {
        const summaries = await pluginApi.list()
        plugins.value = summaries.filter((plugin) => !plugin.isBuiltIn)
    } catch (error) {
        console.error('Failed to load plugins', error)
        loadError.value = t('services.plugins.loadError')
    } finally {
        isLoading.value = false
    }
})
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
