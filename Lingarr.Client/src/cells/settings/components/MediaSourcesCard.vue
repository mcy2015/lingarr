<template>
    <!-- ═══ 媒体源卡(原型 screen-06 B 组 setlist 形态):Sonarr/Radarr 行
         (dot + mono 地址 + 同步计数 + 重新同步 + 编辑模态)、Webhook 折叠说明、
         路径映射行下钻。媒体源同步 cron 行组已迁至自动化批次卡。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="media-sources-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.sources.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.sources.meta') }}
            </span>
        </div>

        <div
            v-for="source in sources"
            :key="source.kind"
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
            :data-test="`source-row-${source.kind}`">
            <span
                class="dot flex-none"
                :class="source.configured ? 'bg-success' : 'bg-line'"></span>
            <b class="flex-none">{{ source.name }}</b>
            <span
                class="mono text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="source.hint">
                {{ source.hint }}
            </span>
            <span class="ml-auto flex flex-none gap-1.5">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="!source.configured || syncing[source.kind]"
                    :data-test="`resync-${source.kind}`"
                    @click="resync(source)">
                    {{ t('settings.sources.resync') }}
                </button>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    :data-test="`edit-source-${source.kind}`"
                    @click="editing = source.kind">
                    {{ t('settings.sources.edit') }}
                </button>
            </span>
        </div>

        <!-- 路径映射行:紧随源行(与同步因果相连),下钻保留页面;hint 双态 =
             映射数 + 不可达体检警示(绑真实磁盘状态,非静态劝导) -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
            data-test="mapping-row">
            <b class="flex-none">{{ t('settings.sources.mapping') }}</b>
            <span class="text-secondary-content mono min-w-0 flex-1 truncate text-xs">
                {{ t('settings.sources.mappingHint', { count: mappingStore.mappings.length }) }}
                <span
                    v-if="mappingStore.unreachableCount > 0"
                    class="text-warning-content"
                    data-test="mapping-unreachable"
                    :title="t('settings.sources.mappingUnreachable', { count: mappingStore.unreachableCount })">
                    · {{ t('settings.sources.mappingUnreachable', { count: mappingStore.unreachableCount }) }}
                </span>
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="mapping-open"
                @click="router.push({ name: 'settings-mapping' })">
                {{ t('settings.sources.manage') }}
            </button>
        </div>

        <!-- Webhook URL 折叠说明区(原型 setlist/meta 层级:折叠头 13px →
             说明 12px hint → 每源一行「名称 b + mono URL」) -->
        <div class="border-line border-b">
            <button
                type="button"
                class="text-secondary-content hover:text-primary-content flex w-full cursor-pointer items-center gap-2 px-4 py-2.5 text-left text-[13px] transition-colors"
                :aria-expanded="webhookOpen"
                data-test="webhook-fold"
                @click="webhookOpen = !webhookOpen">
                <span class="text-xs">{{ webhookOpen ? '▾' : '▸' }}</span>
                {{ t('settings.sources.webhook') }}
                <span class="mono ml-auto hidden text-[11px] sm:inline">/api/webhook</span>
            </button>
            <div v-if="webhookOpen" class="space-y-2 px-4 pb-3">
                <p class="text-secondary-content text-[12px] leading-snug">
                    {{ t('services.webhook.description') }}
                    {{ t('services.webhook.goTo') }}
                    <b>Settings → Connect → Connections → +</b>
                    {{ t('services.webhook.andUseThisUrl') }}
                </p>
                <div
                    class="flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[13px]"
                    data-test="webhook-url-radarr">
                    <b class="flex-none font-semibold">Radarr</b>
                    <CodeSnippet class="mono min-w-0 flex-1 overflow-x-auto text-xs">
                        {{ radarrWebhookUrl }}
                    </CodeSnippet>
                </div>
                <div
                    class="flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[13px]"
                    data-test="webhook-url-sonarr">
                    <b class="flex-none font-semibold">Sonarr</b>
                    <CodeSnippet class="mono min-w-0 flex-1 overflow-x-auto text-xs">
                        {{ sonarrWebhookUrl }}
                    </CodeSnippet>
                </div>
            </div>
        </div>

        <!-- 编辑模态:地址 + API Key + 默认包含(复用原 IntegrationSettings 表单逻辑) -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="editing !== null"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="editing = null"
                    @keydown.escape="editing = null">
                    <div
                        class="bg-secondary border-line shell-scroll max-h-[85vh] w-full max-w-md overflow-y-auto rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        :data-test="`source-modal-${editing}`"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">{{ editingSource?.name }}</h2>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                                @click="editing = null">
                                {{ t('common.close') }}
                            </button>
                        </div>
                        <div v-if="editingSource" class="space-y-3">
                            <InputComponent
                                v-model="editingSource.urlModel.value"
                                :validation-type="INPUT_VALIDATION_TYPE.URL"
                                :label="t('services.integration.address')"
                                :error-message="editingSource.urlError"
                                @update:validation="(val) => setValid(editing, 'url', val)" />
                            <div class="flex items-end gap-1.5">
                                <InputComponent
                                    v-model="editingSource.keyModel.value"
                                    class="min-w-0 flex-1"
                                    :min-length="32"
                                    :max-length="32"
                                    :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                    :type="
                                        revealedKey !== null
                                            ? INPUT_TYPE.TEXT
                                            : INPUT_TYPE.PASSWORD
                                    "
                                    :label="t('services.integration.apiKey')"
                                    :placeholder="editingKeyPlaceholder"
                                    :error-message="
                                        t('services.integration.apiKeyLengthError', { minLength: 32 })
                                    "
                                    @update:validation="(val) => setValid(editing, 'key', val)" />
                                <button
                                    v-if="editingKeyConfigured"
                                    type="button"
                                    class="border-line text-primary-content hover:bg-tertiary mb-[2px] cursor-pointer rounded-md border px-2 py-1.5 transition-colors"
                                    :title="revealedKey !== null ? t('common.hide') : t('common.show')"
                                    :aria-label="revealedKey !== null ? t('common.hide') : t('common.show')"
                                    :data-test="`key-visibility-${editing}`"
                                    @click="toggleKeyReveal()">
                                    <Icon
                                        :name="revealedKey !== null ? 'eyeOn' : 'eyeOff'"
                                        class="h-4 w-4" />
                                </button>
                            </div>
                            <div class="flex items-center space-x-2 pt-1">
                                <ToggleButton v-model="editingSource.includeModel.value">
                                    <span class="text-primary-content text-xs font-medium">
                                        {{ t('services.integration.includeByDefault') }}
                                    </span>
                                </ToggleButton>
                            </div>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import {
    ENCRYPTED_SETTINGS,
    INPUT_TYPE,
    INPUT_VALIDATION_TYPE,
    IPagedResult,
    SETTINGS
} from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { mediaService } from '@/cells/app/api/mediaService'
import { settingService } from '@/cells/app/api/settingService'
import { scheduleService } from '@/cells/settings/services/scheduleService'
import { useMappingStore } from '@/cells/settings/stores/mapping'
import InputComponent from '@/cells/settings/components/InputComponent.vue'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'
import CodeSnippet from '@/cells/kernel/components/CodeSnippet.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import { resolveUrl } from '@/cells/app/utils/baseUrl'

const { t } = useI18n()
const router = useRouter()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const mappingStore = useMappingStore()
const mediaApi = mediaService(axios)
const settingApi = settingService(axios)
const scheduleApi = scheduleService(axios)

const radarrWebhookUrl = resolveUrl('/api/webhook/radarr')
const sonarrWebhookUrl = resolveUrl('/api/webhook/sonarr')

const webhookOpen = ref(false)
const editing = ref<'radarr' | 'sonarr' | null>(null)
const syncing = reactive<{ radarr: boolean; sonarr: boolean }>({ radarr: false, sonarr: false })

// ── 同步计数(真实分页总数)────────────────────────────────────────
const movieCount = ref<number | null>(null)
const showCount = ref<number | null>(null)

onMounted(async () => {
    try {
        mappingStore.fetchMappings()
        mappingStore.fetchStatus()
    } catch {
        // mapping count simply stays at the store default
    }
    try {
        const page = await mediaApi.movies<IPagedResult<unknown>>(1, '', 'title', true)
        movieCount.value = page.totalCount
    } catch {
        // hint falls back to the address only
    }
    try {
        const page = await mediaApi.shows<IPagedResult<unknown>>(1, '', 'title', true)
        showCount.value = page.totalCount
    } catch {
        // hint falls back to the address only
    }
})

const radarrUrl = computed(() => (settingStore.getSetting(SETTINGS.RADARR_URL) as string) ?? '')
const sonarrUrl = computed(() => (settingStore.getSetting(SETTINGS.SONARR_URL) as string) ?? '')

interface ISourceRow {
    kind: 'radarr' | 'sonarr'
    name: string
    configured: boolean
    hint: string
}

const sources = computed<ISourceRow[]>(() => [
    {
        kind: 'radarr',
        name: 'Radarr',
        configured: radarrUrl.value.trim() !== '',
        hint:
            radarrUrl.value.trim() !== ''
                ? `${radarrUrl.value} · ${movieCount.value != null ? t('settings.sources.moviesSynced', { count: movieCount.value }) : '…'}`
                : t('settings.sources.notConfigured')
    },
    {
        kind: 'sonarr',
        name: 'Sonarr',
        configured: sonarrUrl.value.trim() !== '',
        hint:
            sonarrUrl.value.trim() !== ''
                ? `${sonarrUrl.value} · ${showCount.value != null ? t('settings.sources.showsSynced', { count: showCount.value }) : '…'}`
                : t('settings.sources.notConfigured')
    }
])

async function resync(source: ISourceRow): Promise<void> {
    if (syncing[source.kind]) return
    syncing[source.kind] = true
    try {
        if (source.kind === 'radarr') {
            await scheduleApi.indexMovies()
        } else {
            await scheduleApi.indexShows()
        }
        toastStore.push({
            message: t('settings.sources.syncTriggered', { name: source.name }),
            type: 'success'
        })
    } catch (error) {
        toastStore.push({
            message: error instanceof Error ? error.message : String(error),
            type: 'error'
        })
    } finally {
        syncing[source.kind] = false
    }
}

// ── 编辑模态:复用原 IntegrationSettings 的校验/保存逻辑 ─────────────
const radarrValid = reactive({ url: false, key: false })
const sonarrValid = reactive({ url: false, key: false })

/// 敏感值统一策略(输入型):API Key 编辑态不回显旧值(输入恒从空开始,
/// 服务端只回「已配置」布尔),已配置时占位符说明「留空保持不变」;
/// 空输入不写空串覆盖已存密钥。「显示」为用户显式动作:经按 key 的
/// reveal 端点取回明文填入输入框(cookie 会话专用,服务端留审计)。
function keyFor(kind: 'radarr' | 'sonarr' | null) {
    if (kind === 'radarr') return ENCRYPTED_SETTINGS.RADARR_API_KEY
    if (kind === 'sonarr') return ENCRYPTED_SETTINGS.SONARR_API_KEY
    return null
}

function keyConfigured(kind: 'radarr' | 'sonarr' | null): boolean {
    const key = keyFor(kind)
    if (key === null) return false
    return settingStore.isEncryptedSettingConfigured(key)
}

const revealedKey = ref<string | null>(null)

// 切换编辑对象时收回已显示的密钥:显示态只属于当前打开的那一个。
watch(editing, () => {
    revealedKey.value = null
})

async function toggleKeyReveal(): Promise<void> {
    if (!editingKeyConfigured.value) return
    if (revealedKey.value !== null) {
        revealedKey.value = null
        return
    }
    const key = keyFor(editing.value)
    if (key === null) return
    try {
        revealedKey.value = await settingApi.revealEncryptedSetting(key)
    } catch {
        toastStore.push({ message: t('common.loadFailed'), type: 'error' })
    }
}

const editingKeyConfigured = computed(() => keyConfigured(editing.value))
const editingKeyPlaceholder = computed(() =>
    editingKeyConfigured.value ? t('services.integration.apiKeyConfigured') : ''
)

const radarrUrlModel = computed({
    get: (): string => radarrUrl.value,
    set: (newValue: string): void => {
        settingStore.updateSetting(SETTINGS.RADARR_URL, newValue, radarrValid.url)
        if (radarrValid.url) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const radarrKeyModel = computed({
    // 默认不回显(Secret 输入恒从空开始);「显示」后 revealedKey 持有
    // 明文,输入框以文本态展示并可直接编辑保存。
    get: (): string => revealedKey.value ?? '',
    set: (newValue: string): void => {
        // 空输入 = 保持已配置的密钥不变,不做任何写入
        if (newValue === '') return
        settingStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.RADARR_API_KEY,
            newValue,
            radarrValid.key
        )
        if (radarrValid.key) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const radarrIncludeModel = computed({
    get: (): string =>
        (settingStore.getSetting(SETTINGS.RADARR_DEFAULT_INCLUDE) as string) ?? 'false',
    set: (newValue: string): void => {
        settingStore.updateSetting(SETTINGS.RADARR_DEFAULT_INCLUDE, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

const sonarrUrlModel = computed({
    get: (): string => sonarrUrl.value,
    set: (newValue: string): void => {
        settingStore.updateSetting(SETTINGS.SONARR_URL, newValue, sonarrValid.url)
        if (sonarrValid.url) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const sonarrKeyModel = computed({
    // 同 radarr:默认空输入 + 「留空保持不变」,显示态回填明文。
    get: (): string => revealedKey.value ?? '',
    set: (newValue: string): void => {
        // 空输入 = 保持已配置的密钥不变,不做任何写入
        if (newValue === '') return
        settingStore.updateEncryptedSetting(
            ENCRYPTED_SETTINGS.SONARR_API_KEY,
            newValue,
            sonarrValid.key
        )
        if (sonarrValid.key) {
            toastStore.push({ message: t('common.saved'), type: 'success' })
        }
    }
})
const sonarrIncludeModel = computed({
    get: (): string =>
        (settingStore.getSetting(SETTINGS.SONARR_DEFAULT_INCLUDE) as string) ?? 'false',
    set: (newValue: string): void => {
        settingStore.updateSetting(SETTINGS.SONARR_DEFAULT_INCLUDE, newValue, true)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    }
})

function setValid(kind: 'radarr' | 'sonarr' | null, field: 'url' | 'key', value: boolean): void {
    if (kind === 'radarr') radarrValid[field] = value
    else if (kind === 'sonarr') sonarrValid[field] = value
}

const editingSource = computed(() => {
    if (editing.value === 'radarr') {
        return {
            name: 'Radarr',
            urlModel: radarrUrlModel,
            keyModel: radarrKeyModel,
            includeModel: radarrIncludeModel,
            urlError: t('services.integration.radarrUrlError')
        }
    }
    if (editing.value === 'sonarr') {
        return {
            name: 'Sonarr',
            urlModel: sonarrUrlModel,
            keyModel: sonarrKeyModel,
            includeModel: sonarrIncludeModel,
            urlError: t('services.integration.sonarrUrlError')
        }
    }
    return null
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
