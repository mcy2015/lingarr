<template>
    <!-- 语言策略卡(原型 screen-06 C 组,P1-3):目标语言 / 源语言白名单的
         唯一编辑点(自 providers 页迁移)+ 派生只读「链上语言覆盖」行。 -->
    <div class="border-line bg-secondary rounded-md border">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.languagePolicy.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">{{ t('settings.languagePolicy.meta') }}</span>
        </div>

        <div class="space-y-4 p-4" data-test="language-policy">
            <!-- 源/目标语言编辑:同一 store 状态,单一编辑点 -->
            <SourceAndTarget @save="toastStore.push({ message: t('common.saved'), type: 'success' })" />

            <!-- 派生只读行:链上语言覆盖 -->
            <div class="border-line border-t pt-3">
                <div class="flex items-center gap-2">
                    <span class="font-semibold">{{ t('settings.languagePolicy.coverage') }}</span>
                    <span
                        class="text-secondary-content/60 cursor-help text-[10.5px]"
                        :title="t('settings.languagePolicy.coverageHint')">
                        ⓘ
                    </span>
                </div>

                <div v-if="chain.length === 0" class="text-warning mt-2 text-[12.5px]" data-test="coverage-empty">
                    ⚠ {{ t('settings.languagePolicy.chainEmpty') }}
                </div>
                <div v-else-if="loadingTables" class="text-secondary-content/60 mt-2 text-[12.5px]">
                    {{ t('settings.languagePolicy.loading') }}
                </div>
                <div v-else class="mt-2 space-y-1.5">
                    <div
                        v-for="row in coverageRows"
                        :key="`${row.source}-${row.target}`"
                        class="mono text-[12px]"
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
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import SourceAndTarget from '@/cells/settings/components/SourceAndTarget.vue'
import { translateService } from '@/cells/app/api/translateService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { ILanguage, SETTINGS } from '@/types'
import { parseServiceChain } from '@/cells/app/utils/serviceChain'
import { computeChainCoverage, ServiceLanguageTable } from '@/cells/settings/services/chainCoverage'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
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

const coverageRows = computed(() =>
    computeChainCoverage(chain.value, tables.value, sourceCodes.value, targetCodes.value)
)

/// 语言表随链与语言设置变化重取:每次挂载(进 quality 面板)拉一次,
/// 链变动(编辑服务链后回到此页)自然刷新。
onMounted(fetchTables)

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
