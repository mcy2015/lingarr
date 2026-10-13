<template>
    <!-- 语言策略卡(原型 screen-06 C 组 setlist 三行,P1-3):目标语言 chips、
         源语言白名单 mono hint,两行各自的「编辑」模态走与原 SourceAndTarget
         完全相同的设置写回路径;「链上语言覆盖」保持派生只读。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="language-policy-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.languagePolicy.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">{{ t('settings.languagePolicy.meta') }}</span>
        </div>

        <!-- 行 1:目标语言(chips + 编辑) -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
            data-test="lp-row-targets">
            <b class="flex-none">{{ t('settings.languagePolicy.targets') }}</b>
            <span v-if="targetCodes.length === 0" class="text-secondary-content/60 text-xs">
                {{ t('settings.languagePolicy.empty') }}
            </span>
            <span v-else class="flex min-w-0 flex-1 flex-wrap gap-1.5">
                <span
                    v-for="code in targetCodes"
                    :key="code"
                    class="mono flex-none rounded-[3px] border border-success/40 bg-success/10 px-1.5 py-px text-[11px] text-success"
                    :data-test="`lp-target-chip-${code.toUpperCase()}`">
                    {{ code.toUpperCase() }} ✓
                </span>
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="lp-edit-targets"
                @click="openEdit('targets')">
                {{ t('settings.languagePolicy.edit') }}
            </button>
        </div>

        <!-- 行 2:源语言白名单(mono hint + 编辑) -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-2.5 text-[13px]"
            data-test="lp-row-sources">
            <b class="flex-none">{{ t('settings.languagePolicy.sources') }}</b>
            <span
                class="text-secondary-content mono min-w-0 flex-1 truncate text-xs"
                :title="sourceHint"
                data-test="lp-source-hint">
                {{ sourceHint }}
            </span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="lp-edit-sources"
                @click="openEdit('sources')">
                {{ t('settings.languagePolicy.edit') }}
            </button>
        </div>

        <!-- 行 3:链上语言覆盖(派生只读) -->
        <div class="px-4 py-2.5 text-[13px]" data-test="lp-row-coverage">
            <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
                <b class="flex-none">
                    {{ t('settings.languagePolicy.coverage') }}
                    <span
                        class="text-secondary-content/60 cursor-help text-[10.5px]"
                        :title="t('settings.languagePolicy.coverageHint')">
                        ⓘ
                    </span>
                </b>
                <span class="text-secondary-content/60 min-w-0 flex-1 text-[11px]">
                    {{ t('settings.languagePolicy.coverageDerived') }}
                </span>
            </div>

            <div v-if="chain.length === 0" class="text-warning mt-2 text-[12.5px]" data-test="coverage-empty">
                ⚠ {{ t('settings.languagePolicy.chainEmpty') }}
            </div>
            <div v-else-if="loadingTables" class="text-secondary-content/60 mt-2 text-[12.5px]">
                {{ t('settings.languagePolicy.loading') }}
            </div>
            <div v-else class="mono mt-2 space-y-1.5 text-[12px]">
                <div
                    v-for="row in coverageRows"
                    :key="`${row.source}-${row.target}`"
                    :data-test="`coverage-${row.source}-${row.target}`"
                    :title="t('settings.languagePolicy.coverageHint')">
                    <span
                        :class="
                            row.available === 0
                                ? 'text-danger'
                                : row.missing.length > 0
                                  ? 'text-warning'
                                  : 'text-primary-content'
                        ">
                        {{
                            row.available === 0
                                ? `⚠ ${row.source}→${row.target} 0/${row.total}`
                                : `${row.source}→${row.target} ${row.available}/${row.total}`
                        }}
                    </span>
                    <span v-if="row.missing.length > 0" class="text-warning">
                        {{
                            t('settings.languagePolicy.unsupported', {
                                services: row.missing.join(', ')
                            })
                        }}
                    </span>
                    <span v-if="row.unknown > 0" class="text-secondary-content/60">
                        ·
                        {{ t('settings.languagePolicy.unknownCount', { count: row.unknown }) }}
                    </span>
                </div>
                <div v-if="coverageRows.length === 0" class="text-secondary-content/60 text-[12.5px]">
                    {{ t('settings.languagePolicy.noPairs') }}
                </div>
            </div>
        </div>

        <!-- 目标/源语言编辑模态:逗号分隔 ISO 码,保存走 SourceAndTarget 同款
             store 写回(updateSetting isJson)。 -->
        <Teleport to="body">
            <Transition
                enter-active-class="transition-opacity duration-200 ease-in-out"
                leave-active-class="transition-opacity duration-200 ease-in-out"
                enter-from-class="opacity-0"
                leave-to-class="opacity-0">
                <div
                    v-if="editing"
                    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
                    @click="editing = null"
                    @keydown.escape="editing = null">
                    <div
                        class="bg-secondary border-line w-full max-w-md rounded-lg border p-5"
                        role="dialog"
                        aria-modal="true"
                        :aria-label="editingLabel"
                        data-test="lp-modal"
                        @click.stop>
                        <div class="mb-4 flex items-center gap-2.5">
                            <h2 class="text-[15px] font-semibold">{{ editingLabel }}</h2>
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                                data-test="lp-modal-close"
                                @click="editing = null">
                                {{ t('common.close') }}
                            </button>
                        </div>

                        <label class="text-secondary-content mb-1.5 block text-xs" for="lp-codes-input">
                            {{ t('settings.languagePolicy.codesField') }}
                        </label>
                        <input
                            id="lp-codes-input"
                            v-model="draftCodes"
                            type="text"
                            class="border-line bg-primary text-primary-content mono w-full rounded-md border px-2.5 py-1.5 text-[13px] outline-none"
                            placeholder="zh, en-US"
                            data-test="lp-modal-input" />
                        <p class="text-secondary-content mt-2 text-[11.5px] leading-relaxed">
                            {{ t('settings.languagePolicy.variantHint') }}
                        </p>
                        <p v-if="!draftValid" class="text-danger mt-2 text-xs" data-test="lp-modal-error">
                            {{ t('settings.languagePolicy.required') }}
                        </p>

                        <div class="mt-4 flex justify-end gap-2">
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-3 py-1.5 text-xs transition-colors"
                                data-test="lp-modal-cancel"
                                @click="editing = null">
                                {{ t('common.close') }}
                            </button>
                            <button
                                type="button"
                                class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                                :disabled="!draftValid"
                                data-test="lp-modal-save"
                                @click="saveEdit">
                                {{ t('common.saved') }}
                            </button>
                        </div>
                    </div>
                </div>
            </Transition>
        </Teleport>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { translateService } from '@/cells/app/api/translateService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { ILanguage, SETTINGS } from '@/types'
import { parseServiceChain } from '@/cells/app/utils/serviceChain'
import { computeChainCoverage, ServiceLanguageTable } from '@/cells/settings/services/chainCoverage'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const translateStore = useTranslateStore()
const translateApi = translateService(axios)

const tables = ref<Record<string, ServiceLanguageTable>>({})
const loadingTables = ref(false)

const chain = computed(() => parseServiceChain(settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string))

// getSettings parses the language JSON regardless of which load path ran;
// the store type still admits raw strings, so narrow defensively
const toCodes = (value: string | ILanguage[]): string[] =>
    (Array.isArray(value) ? value : []).map((language) => language.code)

const sourceCodes = computed<string[]>(() => toCodes(settingStore.getSettings.source_languages))
const targetCodes = computed<string[]>(() => toCodes(settingStore.getSettings.target_languages))

const sourceHint = computed(() =>
    sourceCodes.value.length > 0 ? sourceCodes.value.map((code) => code.toUpperCase()).join(' · ') : '—'
)

const coverageRows = computed(() =>
    computeChainCoverage(chain.value, tables.value, sourceCodes.value, targetCodes.value)
)

// ── 目标/源语言编辑(与原 SourceAndTarget 相同的写回路径)──────────
type EditTarget = 'targets' | 'sources'

const editing = ref<EditTarget | null>(null)
const draftCodes = ref('')

const editingLabel = computed(() =>
    editing.value === 'targets'
        ? t('settings.languagePolicy.targets')
        : t('settings.languagePolicy.sources')
)

const draftValid = computed(() => draftCodes.value.trim().length > 0)

function openEdit(target: EditTarget): void {
    const codes = target === 'targets' ? targetCodes.value : sourceCodes.value
    draftCodes.value = codes.join(', ')
    editing.value = target
}

/// ISO 码 → ILanguage:优先用全量文化表补显示名(ImportPage 等消费方渲染
/// name),查不到时代码本身兜底。
function toLanguage(code: string): ILanguage {
    const known = translateStore.getLanguages.find(
        (language) => language.code.toLowerCase() === code.toLowerCase()
    )
    return known ?? { code, name: code }
}

async function saveEdit(): Promise<void> {
    if (!editing.value || !draftValid.value) return

    const codes = draftCodes.value
        .split(',')
        .map((code) => code.trim())
        .filter((code) => code.length > 0)
    if (codes.length === 0) return

    const key = editing.value === 'targets' ? SETTINGS.TARGET_LANGUAGES : SETTINGS.SOURCE_LANGUAGES
    const value = codes.map(toLanguage)
    await settingStore.updateSetting(key, value, true, true)
    editing.value = null
    toastStore.push({ message: t('common.saved'), type: 'success' })
}

/// 语言表随链与语言设置变化重取:设置 store 异步加载完成可能晚于本卡
/// 挂载(mount 时链常为空),用 immediate watch 保证链到位即取数,链后续
/// 增删/重排也自然刷新。文化表为编辑模态补显示名。
onMounted(() => {
    void translateStore.setLanguages()
})
watch(chain, () => void fetchTables(), { immediate: true })

async function fetchTables(): Promise<void> {
    const members = [...new Set(chain.value)]
    if (members.length === 0) {
        tables.value = {}
        return
    }
    loadingTables.value = true
    try {
        const results = await Promise.allSettled(members.map((serviceType) => translateApi.getServiceLanguages(serviceType)))
        const next: Record<string, ServiceLanguageTable> = {}
        results.forEach((result, index) => {
            if (result.status === 'fulfilled') {
                next[members[index]] = {
                    serviceType: result.value.serviceType,
                    status: result.value.status,
                    languages: result.value.languages
                }
            } else {
                // transport failure is also just "unknown" for the coverage row
                next[members[index]] = { serviceType: members[index], status: 'unknown', languages: [] }
            }
        })
        tables.value = next
    } finally {
        loadingTables.value = false
    }
}
</script>
