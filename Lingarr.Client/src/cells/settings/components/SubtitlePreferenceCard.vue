<template>
    <!-- ═══ 字幕搜索偏好卡(设置 · 自动化 tab,B07):prefer_hearing_impaired
         单开关(默认关闭)。开关只影响同分候选的排序(听障/SDH 在前),不改
         分数、不改自动下载阈值——关闭时排序与历史行为逐字节一致。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="subtitle-preference-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.subtitlePreference.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.subtitlePreference.description') }}
            </span>
        </div>

        <!-- 听障优先行:同分 tiebreak 开关 -->
        <div
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="prefer-hearing-impaired-row">
            <b class="flex-none">{{ t('settings.subtitlePreference.preferHiLabel') }}</b>
            <span
                class="text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="t('settings.subtitlePreference.preferHiHint')">
                {{ t('settings.subtitlePreference.preferHiHint') }}
            </span>
            <span class="ml-auto flex flex-none items-center gap-2.5">
                <ToggleButton v-model="preferHearingImpaired" size="small">
                    <span class="text-primary-content text-xs font-medium">
                        {{
                            preferHearingImpaired === 'true'
                                ? t('settings.status.enabled')
                                : t('settings.status.disabled')
                        }}
                    </span>
                </ToggleButton>
            </span>
        </div>
    </div>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { settingService } from '@/cells/app/api/settingService'
import { useToastStore } from '@/cells/app/stores/toast'
import ToggleButton from '@/cells/kernel/components/ToggleButton.vue'

/// 必须与服务端 SettingKeys.SubtitleSearch.PreferHearingImpaired 一致
/// (types/setting.ts 的 SETTINGS 清单本批不动,卡片经 setting API 直存直取,
/// PostProcessCard 同一形态)。
const PREFER_HEARING_IMPAIRED_KEY = 'subtitle_search_prefer_hearing_impaired'

const settingApi = settingService(axios)
const toastStore = useToastStore()
const { t } = useI18n()

/// 'true'/'false' 字符串 —— 与服务端设置存储口径一致,默认关闭。
const preferHearingImpaired = ref('false')
const saving = ref(false)

/// 最后一个已知落库的值:水合回显与失败回拨都以它为基线,watch 对
/// 「与基线相同」的赋值直接跳过,不会把回显当作用户改动再存一次。
let persisted = 'false'

onMounted(async () => {
    try {
        const settings = await settingApi.getSettings<Record<string, string>>([
            PREFER_HEARING_IMPAIRED_KEY
        ])
        const stored = settings[PREFER_HEARING_IMPAIRED_KEY]
        if (stored === 'true' || stored === 'false') {
            persisted = stored // baseline first, then the ref
            preferHearingImpaired.value = stored
        }
    } catch {
        // 读取失败保持默认 false —— 不虚构服务端状态。
    }
})

watch(preferHearingImpaired, (value) => {
    if (!saving.value && value !== persisted) {
        void persist(value)
    }
})

async function persist(value: string): Promise<void> {
    saving.value = true
    try {
        await settingApi.setSetting(PREFER_HEARING_IMPAIRED_KEY, value)
        persisted = value
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
        // 回拨到最后已知落库值 —— 界面不谎报被服务端拒绝的状态;
        // 回拨值 === persisted,watch 不会再触发保存。
        preferHearingImpaired.value = persisted
        return
    } finally {
        saving.value = false
    }

    // 保存期间的快速反复拨动:落库值与界面值漂移时补一轮(最终收敛)。
    if (preferHearingImpaired.value !== persisted) {
        void persist(preferHearingImpaired.value)
    }
}
</script>
