<template>
    <div class="w-full flex h-full min-h-0 flex-col gap-3.5 overflow-hidden p-4 lg:p-6">
        <!-- ═══ pb-head(原型 screen-10):← 翻译服务 + 请求模版 · <服务> +
             mono 副行(model/批大小,从 settings store 读,缺则省略)+
             重置为默认 + 保存模版(primary)。 -->
        <PbHead
            :back-to="{ name: 'settings-providers' }"
            :back-label="t('settings.menu.groups.providers')"
            :title="`${t('requestTemplate.title')} · ${currentDisplayName}`"
            :sub="subLine">
            <template #actions>
                <ButtonComponent
                    variant="secondary"
                    size="xs"
                    :disabled="!templateCard?.canReset"
                    data-test="tpl-reset"
                    @click="resetTemplate">
                    {{ t('requestTemplate.reset') }}
                </ButtonComponent>
                <ButtonComponent
                    variant="primary"
                    size="xs"
                    data-test="tpl-save"
                    @click="saveTemplate">
                    {{ t('requestTemplate.save') }}
                </ButtonComponent>
            </template>
        </PbHead>

        <div v-if="tabOptions.length > 1" class="border-line bg-secondary flex shrink-0 flex-wrap items-center gap-3 rounded-md border px-3 py-2">
            <TabComponent
                :model-value="service"
                :options="tabOptions"
                @update:model-value="selectService" />
        </div>

        <div class="shell-scroll grid min-h-0 flex-1 grid-flow-row auto-rows-max grid-cols-1 gap-4 overflow-y-auto pr-1 md:grid-cols-2 xl:grid-cols-2">
            <TranslationPrompt />
            <ProofreadPrompt />
        </div>

        <div class="shell-scroll grid grid-flow-row auto-rows-max grid-cols-1 gap-4 overflow-y-auto pr-1 pb-1">
            <RequestTemplate ref="templateCard" :service="service" />
            <!-- ═══ 试译预览(原型 screen-10 右下卡):单行走真实请求 ═══ -->
            <TranslationPreviewCard :service="service" />
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { IPluginSummary, ISettings, SETTINGS, SERVICE_TYPE } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useUiStore } from '@/cells/app/stores/ui'
import axios from 'axios'
import { pluginService } from '@/cells/settings/services/pluginService'
import PbHead from '@/cells/app/components/PbHead.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import TabComponent from '@/cells/kernel/components/TabComponent.vue'
import RequestTemplate from '@/cells/settings/components/template/RequestTemplate.vue'
import TranslationPreviewCard from '@/cells/settings/components/template/TranslationPreviewCard.vue'
import TranslationPrompt from '@/cells/settings/components/TranslationPrompt.vue'
import ProofreadPrompt from '@/cells/settings/components/ProofreadPrompt.vue'

const pluginApi = pluginService(axios)

const router = useRouter()
const settingsStore = useSettingStore()
const uiStore = useUiStore()
const { t } = useI18n()

const props = defineProps<{ service: string }>()
const allPlugins = ref<IPluginSummary[]>([])
const pluginsLoaded = ref(false)

const templateCard = ref<InstanceType<typeof RequestTemplate> | null>(null)

const settingsLoaded = computed(
    () => settingsStore.getSetting(SETTINGS.SERVICE_TYPE) !== undefined
)

const configuredServices = computed<string[]>(() => {
    const raw = (settingsStore.getSetting(SETTINGS.SERVICE_TYPE) as string) ?? '[]'
    try {
        return JSON.parse(raw) as string[]
    } catch {
        return []
    }
})

const tabOptions = computed(() => {
    const templated = new Map<string, IPluginSummary>()
    for (const plugin of allPlugins.value) {
        if (plugin.hasRequestTemplate) {
            templated.set(plugin.provider, plugin)
        }
    }

    return configuredServices.value
        .filter((provider) => templated.has(provider))
        .map((provider) => {
            const summary = templated.get(provider)!
            return { value: provider, label: summary.displayName }
        })
})

const currentDisplayName = computed(() => {
    if (!pluginsLoaded.value) {
        return props.service
    }
    return (
        tabOptions.value.find((option) => option.value === props.service)?.label ?? props.service
    )
})

/// mono 副行只列 settings store 里真实存在的参数:model(服务专属设置)/
/// 批大小(use_batch_translation + max_batch_size);temperature 与术语注入
/// 无对应设置,省略。
const modelSettingKeys: Record<string, keyof ISettings> = {
    [SERVICE_TYPE.OPENAI]: SETTINGS.OPENAI_MODEL,
    [SERVICE_TYPE.ANTHROPIC]: SETTINGS.ANTHROPIC_MODEL,
    [SERVICE_TYPE.LOCALAI]: SETTINGS.LOCAL_AI_MODEL,
    [SERVICE_TYPE.GEMINI]: SETTINGS.GEMINI_MODEL,
    [SERVICE_TYPE.DEEPSEEK]: SETTINGS.DEEPSEEK_MODEL,
    [SERVICE_TYPE.MISTRAL]: SETTINGS.MISTRAL_MODEL,
    [SERVICE_TYPE.XAI]: SETTINGS.XAI_MODEL
}

const subLine = computed(() => {
    const parts: string[] = []
    const modelKey = modelSettingKeys[props.service]
    const model = modelKey
        ? ((settingsStore.getSetting(modelKey) as string) ?? '').trim()
        : ''
    if (model) {
        parts.push(model)
    }
    const useBatch =
        (settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string) === 'true'
    const batchSize = (settingsStore.getSetting(SETTINGS.MAX_BATCH_SIZE) as string) ?? ''
    if (useBatch && batchSize) {
        parts.push(t('requestTemplate.subBatch', { count: batchSize }))
    }
    return parts.join(' · ')
})

function selectService(value: string) {
    router.replace({
        name: 'request-template',
        params: { service: value }
    })
}

/// pb-head 与顶栏 CTA 共用同一保存动作(同一函数,原型 act 表)。
function saveTemplate(): void {
    templateCard.value?.save()
}

function resetTemplate(): void {
    templateCard.value?.resetToDefault()
}

watch(
    [() => props.service, tabOptions, pluginsLoaded, settingsLoaded],
    ([current, options, loaded, settingsReady]) => {
        if (!loaded || !settingsReady) {
            return
        }
        if (options.length === 0) {
            router.push({ name: 'settings-providers' })
            return
        }
        if (!options.some((option) => option.value === current)) {
            selectService(options[0].value)
        }
    },
    { immediate: true }
)

// topbar: CTA = the same save action; sub = service · model · editing phrase
watch(
    [currentDisplayName, subLine],
    ([displayName]) => {
        uiStore.registerCta(t('requestTemplate.save'), saveTemplate)
        const parts = [displayName, subLine.value, t('requestTemplate.topbarTail')]
        uiStore.setSubOverride(parts.filter(Boolean).join(' · '))
    },
    { immediate: true }
)

onMounted(async () => {
    try {
        allPlugins.value = await pluginApi.list()
    } catch (error) {
        console.error('Failed to load translation provider list', error)
    } finally {
        pluginsLoaded.value = true
    }
})

onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})
</script>
