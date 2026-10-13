<template>
    <div class="space-y-3">
        <!-- ═══ Provider 卡片网格(原型屏 6 .pcard 骨架)═══
             每卡固定四槽 head/meta/usage/ops:meta 恒占一行(URL/model 信息或「未配置」),
             ops 以 mt-auto 钉在卡底 —— 行内等高,统计行与按钮行跨卡对齐。 -->
        <p class="text-secondary-content px-4 text-[12.5px]">
            {{ t('settings.providers.blurb') }}
        </p>
        <div
            class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] items-stretch gap-3 px-4">
            <div
                v-for="card in cards"
                :key="card.provider"
                class="rounded-md border border-line bg-secondary flex flex-col gap-2.5 p-4"
                :class="{ 'opacity-55': !card.configured }">
                <!-- head 槽:dot + 名称 + 默认 tag + 右侧状态 -->
                <div class="flex items-center gap-2.5" :data-test="`card-head-${card.provider}`">
                    <span class="dot" :class="dotClass(card)"></span>
                    <b class="text-[14px] font-semibold">{{ card.displayName }}</b>
                    <span
                        v-if="isDefault(card.provider)"
                        class="bg-accent text-accent-content mono rounded-[3px] px-1.5 py-px text-[10.5px] font-bold">
                        {{ t('settings.providers.default') }}
                    </span>
                    <span class="text-secondary-content ml-auto flex items-center gap-1.5 text-[11.5px]">
                        {{ statusText(card) }}
                    </span>
                </div>
                <!-- meta 槽:恒在的一行 mono 小字 —— 已配置显示 URL/model 信息,
                     未配置显示弱化文案;LibreTranslate 的 URL 归入此槽。 -->
                <div class="min-w-0" :data-test="`card-meta-${card.provider}`">
                    <div
                        class="mono text-secondary-content truncate text-xs"
                        :class="{ 'opacity-70': !card.configured }"
                        :title="metaLine(card)">
                        {{ metaLine(card) }}
                    </div>
                    <div
                        v-if="card.lastError"
                        class="text-danger truncate text-xs"
                        :title="card.lastError">
                        {{ card.lastError }}
                    </div>
                </div>
                <!-- usage 槽:本月/延迟/成功 三格,骨架一致 -->
                <div
                    class="text-secondary-content flex flex-wrap gap-x-5 gap-y-1 text-xs"
                    :data-test="`card-usage-${card.provider}`">
                    <span>
                        {{ t('settings.providers.month') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.monthlyCost != null ? formatCost(card.monthlyCost) : '—' }}
                        </b>
                    </span>
                    <span>
                        {{ t('settings.providers.latency') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.latencyMs != null ? `${Math.round(card.latencyMs)} ms` : '—' }}
                        </b>
                    </span>
                    <span>
                        {{ t('settings.providers.success') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.successRate != null ? `${Math.round(card.successRate * 100)}%` : '—' }}
                        </b>
                    </span>
                </div>
                <!-- ═══ 主动探测内联结果(原型 .test-res):ok 绿 / err 红,mono,含状态·延迟·消息 ═══ -->
                <div
                    v-if="testing[card.provider] || testResults[card.provider]"
                    class="mono rounded border px-2 py-1 text-[11px] leading-relaxed break-words"
                    :class="testLineClass(card.provider)"
                    :data-test="card.provider"
                    :data-ok="testResults[card.provider]?.ok ?? ''">
                    {{ testLine(card.provider) }}
                </div>
                <!-- ops 槽:mt-auto 钉卡底,按钮左对齐,数量差异不影响垂直位置 -->
                <div
                    class="mt-auto flex flex-wrap gap-1.5 pt-1"
                    :data-test="`card-ops-${card.provider}`">
                    <button
                        v-if="card.configured"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                        :disabled="testing[card.provider]"
                        @click="testConnection(card.provider)">
                        {{ testing[card.provider] ? t('settings.providers.testing') : t('settings.providers.test') }}
                    </button>
                    <!-- 请求模版下钻入口(原型 screen-06/10):仅支持模版的服务显示 -->
                    <button
                        v-if="card.configured && templatedProviders.has(card.provider)"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        :data-test="`tpl-entry-${card.provider}`"
                        @click="router.push({ name: 'request-template', params: { service: card.provider } })">
                        {{ t('settings.providers.requestTemplate') }}
                    </button>
                    <button
                        v-if="!isDefault(card.provider) && card.configured"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        :data-test="`set-default-${card.provider}`"
                        @click="setDefault(card.provider)">
                        {{ t('settings.providers.setDefault') }}
                    </button>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        :data-test="`edit-${card.provider}`"
                        @click="openEdit(card.provider)">
                        {{ card.configured ? t('settings.providers.edit') : t('settings.providers.configure') }}
                    </button>
                    <!-- 删除(ops 行末):仅链上服务(移出回退链 + 清空配置);唯一链成员另有守卫。
                         恒 configured 的免配置服务不在链时无删除按钮;上链后出现,删除 = 移出链,
                         卡因 configured 仍默认展示。 -->
                    <button
                        v-if="isDeletable(card.provider)"
                        type="button"
                        class="border-danger/50 text-danger hover:bg-danger/10 focus-visible:ring-danger cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                        :data-test="`delete-${card.provider}`"
                        @click="requestDelete(card.provider)">
                        {{ t('settings.providers.delete') }}
                    </button>
                </div>
            </div>

            <!-- ＋添加服务虚线卡:所有未上链的服务(内置+插件,含已配置的免配置服务),
                 点击加入链尾并展开编辑;全部上链后此卡隐藏 -->
            <div v-if="addCandidates.length > 0" class="relative" data-test="add-service-card">
                <div
                    v-if="addMenuOpen"
                    class="bg-secondary border-line absolute right-0 bottom-full z-20 mb-1 max-h-64 w-full min-w-[220px] overflow-y-auto rounded-md border p-1 shadow-lg">
                    <button
                        v-for="candidate in addCandidates"
                        :key="candidate.provider"
                        type="button"
                        class="hover:bg-tertiary block w-full cursor-pointer rounded px-2.5 py-1.5 text-left text-xs transition-colors"
                        :data-test="`add-candidate-${candidate.provider}`"
                        @click="addService(candidate.provider)">
                        <b>{{ candidate.displayName }}</b>
                        <span v-if="!candidate.isBuiltIn" class="text-secondary-content ml-1">
                            · {{ t('settings.providers.externalPlugins') }}
                        </span>
                    </button>
                </div>
                <button
                    type="button"
                    class="border-line text-secondary-content hover:border-accent hover:text-primary-content flex h-full min-h-[120px] w-full cursor-pointer flex-col items-start justify-center gap-1.5 rounded-md border border-dashed p-4 text-left transition-colors"
                    @click="addMenuOpen = !addMenuOpen">
                    <b class="text-[13px]">{{ t('settings.providers.addService') }}</b>
                    <span class="text-[11.5px]">
                        {{
                            t('settings.providers.addServiceHint', {
                                builtin: addCandidates.filter((c) => c.isBuiltIn).length,
                                plugins: addCandidates.filter((c) => !c.isBuiltIn).length
                            })
                        }}
                    </span>
                </button>
            </div>
        </div>

        <!-- ═══ 回退顺序卡(A2):service_type 数组唯一编辑点 ═══ -->
        <FallbackOrderCard :health="health" class="px-4" />

        <!-- ═══ 请求与重试(A3):批量/超时/重试/重试退避,全量保留(行式卡) ═══ -->
        <TranslationSettings class="px-4" />

        <!-- ═══ 外部插件收纳行(A3):计数 + 下钻保留页面 ═══ -->
        <div
            class="border-line bg-secondary mx-4 flex max-w-[640px] flex-wrap items-center gap-2.5 rounded-md border px-4 py-3">
            <b class="text-[13px] font-semibold">{{ t('settings.providers.externalPlugins') }}</b>
            <span
                v-if="pluginsLoaded"
                class="mono text-secondary-content rounded-[3px] border border-line bg-tertiary px-1.5 py-px text-[10.5px]"
                data-test="plugins-count">
                {{ t('settings.providers.pluginsCount', { count: externalPlugins.length }) }}
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="plugins-open"
                @click="router.push({ name: 'settings-plugins' })">
                {{ t('settings.providers.open') }}
            </button>
        </div>

        <!-- ═══ 服务凭据编辑模态(A3):DynamicPluginForm ═══ -->
        <ProviderEditModal
            :is-open="editProvider !== null"
            :provider="editProvider ?? ''"
            :display-name="editDisplayName"
            @close="closeEdit" />

        <!-- ═══ 删除服务确认模态:说明后果(移出回退链 + 清空配置)═══ -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="deleteCandidate !== null"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="cancelDelete"
                    @keydown.escape="cancelDelete">
                    <div
                        class="bg-secondary border-line w-full max-w-md rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        :aria-label="t('settings.providers.deleteTitle', { service: deleteDisplayName })"
                        data-test="provider-delete-modal"
                        @click.stop>
                        <div class="mb-3 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">
                                {{ t('settings.providers.deleteTitle', { service: deleteDisplayName }) }}
                            </h2>
                        </div>
                        <p class="text-secondary-content text-[13px] leading-relaxed">
                            {{ t('settings.providers.deleteBody') }}
                        </p>
                        <div class="mt-5 flex justify-end gap-2">
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                                data-test="provider-delete-cancel"
                                @click="cancelDelete">
                                {{ t('settings.providers.deleteCancel') }}
                            </button>
                            <button
                                type="button"
                                class="border-danger/50 text-danger hover:bg-danger/10 focus-visible:ring-danger cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50"
                                data-test="provider-delete-confirm"
                                :disabled="deleting"
                                @click="confirmDelete">
                                {{ deleting ? t('settings.providers.deleting') : t('settings.providers.deleteConfirm') }}
                            </button>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import FallbackOrderCard from '@/cells/settings/components/FallbackOrderCard.vue'
import ProviderEditModal from '@/cells/settings/components/ProviderEditModal.vue'
import TranslationSettings from '@/cells/settings/components/TranslationSettings.vue'
import { translateService } from '@/cells/app/api/translateService'
import { pluginService } from '@/cells/settings/services/pluginService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import useServiceChain from '@/cells/settings/composables/useServiceChain'
import {
    IEncryptedSettings,
    ISettings,
    ITranslationServiceHealth,
    ITranslationTestResponse,
    IPluginSummary,
    PLUGIN_SETTING_TYPE,
    SETTINGS
} from '@/types'

const translateApi = translateService(axios)
const pluginApi = pluginService(axios)

const router = useRouter()
const { t, locale } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()

const health = ref<ITranslationServiceHealth[]>([])
const plugins = ref<IPluginSummary[]>([])
const pluginsLoaded = ref(false)

/// Providers whose request template is editable (drill-down screen 10);
/// drives the「请求模版」entry on the provider card.
const templatedProviders = computed(
    () => new Set(plugins.value.filter((p) => p.hasRequestTemplate).map((p) => p.provider))
)

// ── 主动测试连接(原型 screen-06 .test-res)──────────────────────
const testing = reactive<Record<string, boolean>>({})
const testResults = ref<Record<string, ITranslationTestResponse>>({})

async function testConnection(provider: string): Promise<void> {
    if (testing[provider]) return
    testing[provider] = true
    delete testResults.value[provider]
    try {
        testResults.value[provider] = await translateApi.testConnection(provider)
    } catch (error) {
        testResults.value[provider] = {
            ok: false,
            status: null,
            latencyMs: 0,
            message: error instanceof Error ? error.message : String(error)
        }
    } finally {
        testing[provider] = false
    }
}

function testLineClass(provider: string): string {
    if (testing[provider]) {
        return 'text-secondary-content border-line'
    }
    return testResults.value[provider]?.ok
        ? 'text-success border-success/40 bg-success/10'
        : 'text-danger border-danger/40 bg-danger/10'
}

function testLine(provider: string): string {
    if (testing[provider]) {
        return t('settings.providers.testing')
    }
    const result = testResults.value[provider]
    if (!result) return ''
    const mark = result.ok ? '✓' : '✗'
    const status = result.status != null ? ` ${result.status}` : ''
    return `${mark}${status} · ${Math.round(result.latencyMs)} ms · ${result.message}`
}

// ── 卡片展示规则(数据驱动,不硬编码服务名单):configured(免配置四服务
// 健康行恒 true;LibreTranslate 配了 URL 亦 true)或在 service_type 链上
// 才渲染卡;未配置且未上链的服务只能经「＋添加服务」进链后出现。
// 链空时免配置服务仍恒在,网格天然不空。
const cards = computed(() =>
    health.value
        .filter((row) => row.configured || isOnChain(row.provider))
        .sort((a, b) => Number(b.configured) - Number(a.configured) || a.displayName.localeCompare(b.displayName))
)

// ── 默认服务 / 回退链(A1:service_type 始终是 JSON 数组,首位 = 默认)──
const { chain, isOnChain, offChain } = useServiceChain()

const defaultProvider = computed(() => chain.value[0] ?? '')

function isDefault(provider: string): boolean {
    return defaultProvider.value === provider
}

function displayNameFor(provider: string): string {
    return (
        plugins.value.find((p) => p.provider === provider)?.displayName ??
        health.value.find((row) => row.provider === provider)?.displayName ??
        provider
    )
}

/**
 * A1(截链 bug 修复):旧实现把 service_type 写成裸字符串,多服务链被截断。
 * 现在把目标服务移到数组首位后整体写回,链保持完整。
 */
async function setDefault(provider: string): Promise<void> {
    const current = chain.value
    if (current[0] === provider) return
    const next = [provider, ...current.filter((member) => member !== provider)]
    await settingStore.updateSetting(SETTINGS.SERVICE_TYPE, JSON.stringify(next), true)
    toastStore.push({
        message: t('settings.providers.defaultSwitched', { service: displayNameFor(provider) }),
        type: 'success'
    })
}

onMounted(async () => {
    try {
        health.value = await translateApi.getHealth()
    } catch {
        // the forms below still work without the health glance
    }
    try {
        plugins.value = await pluginApi.list()
        pluginsLoaded.value = true
    } catch {
        // the template entry simply stays hidden without the plugin list
    }
})

function dotClass(card: ITranslationServiceHealth): string {
    if (!card.configured) return 'bg-line'
    if (card.successRate == null) return 'bg-success'
    if (card.successRate >= 0.8) return 'bg-success'
    if (card.successRate >= 0.5) return 'bg-warning'
    return 'bg-danger'
}

function statusText(card: ITranslationServiceHealth): string {
    if (!card.configured) return t('overview.health.offline')
    if (card.successRate == null) return t('settings.providers.idle')
    if (card.successRate >= 0.8) return t('settings.providers.online')
    if (card.successRate >= 0.5) return t('settings.providers.degraded')
    return t('settings.providers.failing')
}

// ── meta 槽:服务专属 model/URL + 批大小;读不到时弱化占位 ──────────
const modelSettingKeys: Partial<Record<string, keyof ISettings>> = {
    openai: SETTINGS.OPENAI_MODEL,
    anthropic: SETTINGS.ANTHROPIC_MODEL,
    localai: SETTINGS.LOCAL_AI_MODEL,
    gemini: SETTINGS.GEMINI_MODEL,
    deepseek: SETTINGS.DEEPSEEK_MODEL,
    mistral: SETTINGS.MISTRAL_MODEL,
    xai: SETTINGS.XAI_MODEL,
    libretranslate: SETTINGS.LIBRETRANSLATE_URL
}

function modelLine(provider: string): string {
    if (!provider) return ''
    const parts: string[] = []
    const modelKey = modelSettingKeys[provider]
    if (modelKey) {
        const model = ((settingStore.getSetting(modelKey) as string) ?? '').trim()
        if (model) parts.push(model)
    }
    const useBatch =
        (settingStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string) === 'true'
    const batchSize = ((settingStore.getSetting(SETTINGS.MAX_BATCH_SIZE) as string) ?? '').trim()
    if (useBatch && batchSize) {
        parts.push(t('requestTemplate.subBatch', { count: batchSize }))
    }
    return parts.join(' · ')
}

/// 恒定一行的 meta 文案:未配置 → 弱化「未配置」;已配置 → URL/model
/// 信息(如 gpt-4o-mini · 批 500、http://libretranslate:5000),无则「—」。
function metaLine(card: ITranslationServiceHealth): string {
    if (!card.configured) return t('settings.providers.notConfigured')
    return modelLine(card.provider) || '—'
}

// ── ＋添加服务(A3):所有未上链的服务(内置+插件)──
const addMenuOpen = ref(false)

const externalPlugins = computed(() => plugins.value.filter((p) => !p.isBuiltIn))

/// 候选 = 全部未上链服务,不再排除已配置者 —— 用户应能把 Bing 等免配置
/// 服务加入回退链;与 FallbackOrderCard 快捷添加共用 offChain 派生(那里
/// 收紧到 configured)。添加动作 = 追加链尾。
const addCandidates = computed(() => offChain(plugins.value))

async function addService(provider: string): Promise<void> {
    addMenuOpen.value = false
    if (chain.value.includes(provider)) return
    await settingStore.updateSetting(
        SETTINGS.SERVICE_TYPE,
        JSON.stringify([...chain.value, provider]),
        true
    )
    openEdit(provider)
}

// ── 编辑模态(A3)──────────────────────────────────────────────
const editProvider = ref<string | null>(null)

const editDisplayName = computed(
    () =>
        plugins.value.find((p) => p.provider === editProvider.value)?.displayName ??
        health.value.find((row) => row.provider === editProvider.value)?.displayName ??
        editProvider.value ??
        ''
)

function openEdit(provider: string): void {
    editProvider.value = provider
}

function closeEdit(): void {
    editProvider.value = null
}

// ── 删除服务:确认模态 → 链移除 + 配置清空 + 刷新 health ───────────
const deleteCandidate = ref<string | null>(null)
const deleting = ref(false)

const deleteDisplayName = computed(() =>
    deleteCandidate.value !== null ? displayNameFor(deleteCandidate.value) : ''
)

/// 仅链上服务可删除(移出回退链 + 清空配置)。恒 configured 的免配置服务
/// 不在链时删除无意义;在链时删除 = 移出链,卡因 configured 仍默认展示。
function isDeletable(provider: string): boolean {
    return isOnChain(provider)
}

function requestDelete(provider: string): void {
    // 唯一链成员守卫:删空链会让默认服务悬空
    if (chain.value.includes(provider) && chain.value.length <= 1) {
        toastStore.push({ message: t('settings.providers.deleteLastGuard'), type: 'error' })
        return
    }
    deleteCandidate.value = provider
}

function cancelDelete(): void {
    deleteCandidate.value = null
}

/**
 * 执行删除:
 * 1) 从 service_type JSON 数组移除该服务(FallbackOrderCard 同款读写);
 * 2) 按 manifest 把该服务的全部设置写空串(Secret 走加密设置),卡片回到未配置态;
 * 3) 重新拉取 health;链由 store 派生自动刷新。
 * 删除链首时新链首自动成为默认服务,toast 说明。
 */
async function confirmDelete(): Promise<void> {
    const provider = deleteCandidate.value
    if (provider === null || deleting.value) return
    deleting.value = true
    try {
        const wasDefault = chain.value[0] === provider
        const nextChain = chain.value.filter((member) => member !== provider)
        // capture names before the health refresh drops the deleted provider
        const name = displayNameFor(provider)
        const successorName = nextChain.length > 0 ? displayNameFor(nextChain[0]) : ''
        if (nextChain.length !== chain.value.length) {
            await settingStore.updateSetting(
                SETTINGS.SERVICE_TYPE,
                JSON.stringify(nextChain),
                true
            )
        }

        let clearedSettings = false
        try {
            const manifest = await pluginApi.getManifest(provider)
            for (const field of manifest.settings) {
                if (field.type === PLUGIN_SETTING_TYPE.SECRET) {
                    await settingStore.updateEncryptedSetting(
                        field.key as keyof IEncryptedSettings,
                        '',
                        true
                    )
                } else {
                    await settingStore.updateSetting(field.key as keyof ISettings, '', true)
                }
            }
            clearedSettings = true
        } catch {
            // manifest 拉不到时仍完成链移除,配置清空失败随 toast 一并说明
        }

        deleteCandidate.value = null
        try {
            health.value = await translateApi.getHealth()
        } catch {
            // keep the previous glance; the chain already reflects the removal
        }

        if (clearedSettings) {
            if (wasDefault && nextChain.length > 0) {
                toastStore.push({
                    message: t('settings.providers.deleteDefaultMigrated', {
                        service: name,
                        next: successorName
                    }),
                    type: 'success'
                })
            } else {
                toastStore.push({
                    message: t('settings.providers.deleteDone', { service: name }),
                    type: 'success'
                })
            }
        } else {
            toastStore.push({ message: t('settings.providers.deletePartial', { service: name }), type: 'error' })
        }
    } catch {
        // the chain write itself failed — keep the modal open so the user can retry
        toastStore.push({ message: t('settings.providers.deleteFailed', { service: displayNameFor(provider) }), type: 'error' })
    } finally {
        deleting.value = false
    }
}

function formatCost(value: number): string {
    return new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 2
    }).format(value)
}
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
    flex: none;
}
</style>
