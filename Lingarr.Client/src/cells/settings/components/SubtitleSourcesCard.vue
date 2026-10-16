<template>
    <!-- ═══ 字幕源卡:GET /api/subtitle-search/providers 数据驱动渲染全部字幕源
         (不硬编码源名单)。本期只把 type=secret 的设置字段放进 UI(PM 裁决:
         BaseUrl 等 url 字段不进),secret 字段复用 DynamicPluginForm/PluginField
         的加密写入与按需 reveal;无 secret 字段的源渲染为紧凑单行(名称+描述+
         测试按钮),不放表单不放徽章。描述文案全部来自 API,无静态装饰句。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="subtitle-sources-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.subtitleSources.title') }}</h2>
            <span
                v-if="loaded && providers.length > 0"
                class="text-secondary-content ml-auto text-[11px]"
                data-test="subtitle-sources-count">
                {{ t('settings.subtitleSources.meta', { count: providers.length }) }}
            </span>
        </div>

        <!-- 诚实状态行:加载中 / 加载失败(可重试)/ 空列表 -->
        <div
            v-if="loadFailed && providers.length === 0"
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3 text-[13px]"
            data-test="subtitle-sources-error">
            <span class="text-danger">{{ t('settings.subtitleSources.loadFailed') }}</span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="subtitle-sources-retry"
                @click="fetchProviders">
                {{ t('settings.subtitleSources.retry') }}
            </button>
        </div>
        <div
            v-else-if="!loaded"
            class="text-secondary-content px-4 py-3 text-[13px]"
            data-test="subtitle-sources-loading">
            {{ t('settings.subtitleSources.loading') }}
        </div>
        <div
            v-else-if="rows.length === 0"
            class="text-secondary-content px-4 py-3 text-[13px]"
            data-test="subtitle-sources-empty">
            {{ t('settings.subtitleSources.empty') }}
        </div>

        <div
            v-for="row in rows"
            :key="row.provider"
            class="border-line border-b px-4 py-2.5 last:border-b-0"
            :data-test="`subtitle-source-row-${row.provider}`">
            <!-- 紧凑头行:名称 + 配置徽章(仅含 secret 字段的源)+ 冷却警示 chip
                 + API 描述 + 操作 -->
            <div class="flex flex-wrap items-center gap-x-3 gap-y-2 text-[13px]">
                <b class="flex-none">{{ row.displayName }}</b>
                <span
                    v-if="row.secretFields.length > 0"
                    class="flex-none"
                    :class="badgeClass(row.credentialsConfigured)"
                    :data-test="`subtitle-source-badge-${row.provider}`">
                    {{
                        row.credentialsConfigured
                            ? t('settings.subtitleSources.configured')
                            : t('settings.subtitleSources.notConfigured')
                    }}
                </span>
                <!-- 冷却健康态(设计⑧):非冷却源不渲染;到期时刻按库页绝对时间
                     习惯显示,401/403 的「直到凭证变更」哨兵改以原因文案呈现 -->
                <span
                    v-if="row.cooldownUntil"
                    class="mono border-warning/40 bg-warning/10 text-warning-content inline-flex flex-none items-center rounded border px-1 py-px text-[10px]"
                    :title="cooldownTitle(row)"
                    :data-test="`subtitle-source-cooldown-${row.provider}`">
                    {{ cooldownLabel(row) }}
                </span>
                <span
                    v-if="row.description"
                    class="text-secondary-content min-w-0 flex-1 truncate text-xs"
                    :title="row.description">
                    {{ row.description }}
                </span>
                <span class="ml-auto flex flex-none gap-1.5">
                    <button
                        v-if="row.secretFields.length > 0"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        :aria-expanded="expanded.has(row.provider)"
                        :data-test="`subtitle-source-toggle-${row.provider}`"
                        @click="toggleExpand(row.provider)">
                        {{
                            expanded.has(row.provider)
                                ? t('settings.subtitleSources.collapse')
                                : t('settings.subtitleSources.expand')
                        }}
                    </button>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                        :disabled="testing[row.provider]"
                        :data-test="`subtitle-source-test-${row.provider}`"
                        @click="runTest(row.provider)">
                        {{
                            testing[row.provider]
                                ? t('settings.subtitleSources.testing')
                                : t('settings.subtitleSources.testConnection')
                        }}
                    </button>
                </span>
            </div>

            <!-- 主动测试内联结果:UX 与 SettingsProvidersPage 同款(ok 绿/err 红,
                 成功带延迟数值,失败带 message;501 等非 2xx 同按失败行处理) -->
            <div
                v-if="testing[row.provider] || testResults[row.provider]"
                class="mono rounded border px-2 py-1 mt-2 text-[11px] leading-relaxed break-words"
                :class="testLineClass(row.provider)"
                :data-test="`subtitle-source-test-result-${row.provider}`"
                :data-ok="testResults[row.provider]?.ok ?? ''">
                {{ testLine(row.provider) }}
            </div>

            <!-- 可展开的凭据表单:仅 secret 字段(加密写入走 PluginField) -->
            <div
                v-if="expanded.has(row.provider)"
                class="border-line mt-2.5 border-t pt-3"
                :data-test="`subtitle-source-form-${row.provider}`">
                <DynamicPluginForm :manifest="row.formManifest" @save="onSaved" />
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import {
    IPluginManifest,
    IPluginSettingField,
    ISubtitleProviderHealthState,
    PLUGIN_SETTING_TYPE
} from '@/types'
import { subtitleSearchService } from '@/cells/app/api/subtitleSearchService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { formatDateTime } from '@/cells/app/utils/date'
import DynamicPluginForm from '@/cells/settings/components/DynamicPluginForm.vue'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()

// ── 数据装载(数据驱动,不硬编码源名单)──────────────────────────
const providers = ref<ISubtitleProviderHealthState[]>([])
const loaded = ref(false)
const loadFailed = ref(false)

// ── 行派生:仅 secret 字段进 UI(url 等字段本期不进)───────────────
/// 线上 type 的大小写两种形态都收(契约示例为小写 'secret',插件侧枚举
/// 序列化为 'Secret'),统一归一成 PLUGIN_SETTING_TYPE.SECRET 再交给
/// DynamicPluginForm,保证 PluginField 走密码输入 + reveal 通道。
function isSecretField(field: IPluginSettingField): boolean {
    return field.type.toLowerCase() === PLUGIN_SETTING_TYPE.SECRET.toLowerCase()
}

interface ISubtitleSourceRow {
    provider: string
    displayName: string
    description: string | null
    credentialsConfigured: boolean
    secretFields: IPluginSettingField[]
    formManifest: IPluginManifest
    cooldownUntil: string | null
    cooldownReason: string | null
}

const rows = computed<ISubtitleSourceRow[]>(() =>
    providers.value.map((source) => {
        const secretFields = source.settings
            .filter(isSecretField)
            .map((field) => ({ ...field, type: PLUGIN_SETTING_TYPE.SECRET }))
        return {
            provider: source.provider,
            displayName: source.displayName,
            description: source.description,
            credentialsConfigured: source.credentialsConfigured,
            secretFields,
            // 浅拷贝 manifest:settings 只留 secret 字段;描述已在行头展示,
            // 表单内不再重复渲染。
            formManifest: {
                provider: source.provider,
                displayName: source.displayName,
                description: null,
                isBuiltIn: true,
                hasRequestTemplate: false,
                settings: secretFields
            },
            // 冷却健康态(设计⑧):`?? null` 让旧载荷(无此字段)按健康渲染
            cooldownUntil: source.cooldownUntil ?? null,
            cooldownReason: source.cooldownReason ?? null
        }
    })
)

// ── 冷却健康态(设计⑧)──────────────────────────────────────────
/// 冷却原因枚举名 → 卡片文案的静态映射表(不做字符串拼键);映射外的原因
/// 名(更新的服务端)原样回显,绝不吐键名或空白。
const COOLDOWN_REASON_LABELS: Record<string, string> = {
    RateLimited: 'settings.subtitleSources.cooldownReason.RateLimited',
    Unauthorized: 'settings.subtitleSources.cooldownReason.Unauthorized',
    ServerError: 'settings.subtitleSources.cooldownReason.ServerError',
    Timeout: 'settings.subtitleSources.cooldownReason.Timeout',
    NetworkError: 'settings.subtitleSources.cooldownReason.NetworkError'
}

function cooldownReasonLabel(reason: string): string {
    const key = COOLDOWN_REASON_LABELS[reason]
    return key ? t(key) : reason
}

/// chip 文案:「冷却中 · 到期时刻」。401/403 的冷却哨兵是「直到凭证变更」
/// 的远期时间戳(9999 年),直接打印年份是装饰——改以原因语义文案呈现;
/// 其余打绝对到期时刻(与库页/事件时间线的本地化格式一致)。
function cooldownLabel(row: ISubtitleSourceRow): string {
    if (row.cooldownReason === 'Unauthorized') {
        return t('settings.subtitleSources.cooldownUntilCredentials')
    }
    return t('settings.subtitleSources.cooldownUntil', {
        time: row.cooldownUntil ? formatDateTime(row.cooldownUntil) : ''
    })
}

/// 原因经 title 提示补全(悬停可见失败分桶)。
function cooldownTitle(row: ISubtitleSourceRow): string {
    return row.cooldownReason ? cooldownReasonLabel(row.cooldownReason) : ''
}

async function fetchProviders(): Promise<void> {
    loadFailed.value = false
    try {
        providers.value = await subtitleSearchService.getProviders()
        loaded.value = true
        await syncSecretStates()
    } catch {
        // 首次失败显示可重试的错误行;刷新失败(已有数据)保留旧行不惊扰
        if (providers.value.length === 0) loadFailed.value = true
    }
}

/// 把 secret 字段的「已配置」布尔装载进 setting store,PluginField 的
/// reveal 按钮与「留空保持不变」占位符依赖它(与 ProviderEditModal 同款)。
async function syncSecretStates(): Promise<void> {
    const secretFields = rows.value.flatMap((row) => row.secretFields)
    if (secretFields.length === 0) return
    try {
        await settingStore.setPluginSettings(secretFields)
    } catch {
        // 徽章以 providers 响应为准,store 同步失败不影响卡片
    }
}

onMounted(fetchProviders)

// ── 展开/收起(secret 源的凭据表单)──────────────────────────────
const expanded = ref<Set<string>>(new Set())

function toggleExpand(provider: string): void {
    const next = new Set(expanded.value)
    if (next.has(provider)) next.delete(provider)
    else next.add(provider)
    expanded.value = next
}

// ── 保存后刷新(徽章跟随 credentialsConfigured 更新)──────────────
async function onSaved(): Promise<void> {
    toastStore.push({ message: t('common.saved'), type: 'success' })
    await fetchProviders()
}

// ── 主动测试连接(UX 对齐 SettingsProvidersPage .test-res)────────
interface ISubtitleTestResult {
    ok: boolean
    latencyMs: number
    message: string
}

const testing = reactive<Record<string, boolean>>({})
const testResults = ref<Record<string, ISubtitleTestResult>>({})

async function runTest(provider: string): Promise<void> {
    if (testing[provider]) return
    testing[provider] = true
    delete testResults.value[provider]
    try {
        testResults.value[provider] = await subtitleSearchService.testConnection<ISubtitleTestResult>(
            provider
        )
    } catch (error) {
        // 501(源不支持探测)与其它非 2xx 同路:内联失败行,不升格为错误态
        testResults.value[provider] = { ok: false, latencyMs: 0, message: extractMessage(error) }
    } finally {
        testing[provider] = false
    }
}

function extractMessage(error: unknown): string {
    if (axios.isAxiosError(error)) {
        const data = error.response?.data as { message?: string; title?: string } | undefined
        return data?.message ?? data?.title ?? error.message
    }
    return error instanceof Error ? error.message : String(error)
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
    if (testing[provider]) return t('settings.subtitleSources.testing')
    const result = testResults.value[provider]
    if (!result) return ''
    if (result.ok) {
        const parts = [t('settings.subtitleSources.testOk'), `${Math.round(result.latencyMs)} ms`]
        if (result.message) parts.push(result.message)
        return parts.join(' · ')
    }
    return [t('settings.subtitleSources.testFailed'), result.message]
        .filter(Boolean)
        .join(' · ')
}

// ── 配置徽章(形式对齐 LibraryPage 的 mono 圆角小徽章)───────────
function badgeClass(configured: boolean): string {
    return configured
        ? 'mono inline-flex items-center rounded border border-success/40 bg-success/10 px-1 py-px text-[10px] text-success-content'
        : 'mono border-line/60 text-secondary-content/70 inline-flex items-center rounded border border-dashed px-1 py-px text-[10px]'
}
</script>
