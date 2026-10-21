<template>
    <!-- ═══ 后处理卡(设置 · 自动化 tab,B01):落盘后繁简模式单选
         none/simplified/traditional(与导入选项同词汇,默认 none)+ 存量批处理
         入口(预览计数 → 确认执行,SubtitleNormalizeService 的两段式形态)。
         所有数字来自服务端真实计数,无静态装饰文案。 -->
    <div class="border-line bg-secondary rounded-md border" data-test="postprocess-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.postProcess.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.postProcess.description') }}
            </span>
        </div>

        <!-- 繁简模式行:新下载字幕落盘后的转换模式 -->
        <div
            class="border-line flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="postprocess-mode-row">
            <b class="flex-none">{{ t('settings.postProcess.modeLabel') }}</b>
            <span
                class="text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="t('settings.postProcess.modeHint')">
                {{ t('settings.postProcess.modeHint') }}
            </span>
            <select
                v-model="mode"
                class="mono border-line bg-tertiary text-secondary-content cursor-pointer rounded border px-1.5 py-0.5 text-[11px]"
                data-test="postprocess-mode"
                :disabled="savingMode"
                @change="saveMode">
                <option value="none">{{ t('import.chinese.none') }}</option>
                <option value="simplified">{{ t('import.chinese.simplified') }}</option>
                <option value="traditional">{{ t('import.chinese.traditional') }}</option>
            </select>
        </div>

        <!-- 存量批处理行:预览计数(不写盘)→ 确认执行(逐文件幂等) -->
        <div
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-[13px]"
            data-test="postprocess-batch-row">
            <b class="flex-none">{{ t('settings.postProcess.batchLabel') }}</b>
            <span
                class="text-secondary-content min-w-0 flex-1 truncate text-xs"
                :title="t('settings.postProcess.batchHint')">
                {{ t('settings.postProcess.batchHint') }}
            </span>
            <span v-if="preview" class="text-secondary-content mono flex-none text-xs" data-test="postprocess-preview-result">
                {{
                    preview.wouldChange > 0
                        ? t('settings.postProcess.previewResult', {
                              changed: preview.wouldChange,
                              scanned: preview.scanned
                          })
                        : t('settings.postProcess.previewNoChange', { scanned: preview.scanned })
                }}
            </span>
            <span v-if="executed" class="text-secondary-content mono flex-none text-xs" data-test="postprocess-execute-result">
                {{
                    t('settings.postProcess.executed', {
                        converted: executed.converted,
                        skipped: executed.skipped,
                        failed: executed.failed.length
                    })
                }}
            </span>
            <span class="ml-auto flex flex-none items-center gap-2">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                    data-test="postprocess-preview"
                    :disabled="!canBatch || previewing || executing"
                    @click="runPreview">
                    {{ t('settings.postProcess.preview') }}
                </button>
                <button
                    type="button"
                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-2.5 py-1 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                    data-test="postprocess-execute"
                    :disabled="!canConfirm || previewing || executing"
                    :title="canConfirm ? '' : t('settings.postProcess.confirmHint')"
                    @click="runExecute">
                    {{ t('settings.postProcess.execute') }}
                </button>
            </span>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { settingService } from '@/cells/app/api/settingService'
import { useToastStore } from '@/cells/app/stores/toast'
import {
    IPostProcessExecuteResponse,
    IPostProcessPreviewResponse,
    postProcessService
} from '@/cells/settings/services/postProcessService'

/// 必须与服务端 SettingKeys.SubtitleSearch.PostProcessChineseVariant 一致
/// (types/setting.ts 的 SETTINGS 清单本批不动,卡片经 setting API 直存直取)。
const POST_PROCESS_VARIANT_KEY = 'subtitle_postprocess_chinese_variant'

const settingApi = settingService(axios)
const batchApi = postProcessService(axios)
const toastStore = useToastStore()
const { t } = useI18n()

const mode = ref('none')
const savingMode = ref(false)
const preview = ref<IPostProcessPreviewResponse | null>(null)
const executed = ref<IPostProcessExecuteResponse | null>(null)
const previewing = ref(false)
const executing = ref(false)

/// 批处理仅在选定具体变体后可用 —— 服务端同样拒绝 none。
const canBatch = computed(() => mode.value === 'simplified' || mode.value === 'traditional')

/// 确认执行必须先有一次预览(预览计数 → 确认执行的两段式)。
const canConfirm = computed(() => canBatch.value && preview.value !== null)

onMounted(async () => {
    try {
        const settings = await settingApi.getSettings<Record<string, string>>([POST_PROCESS_VARIANT_KEY])
        const stored = settings[POST_PROCESS_VARIANT_KEY]
        if (stored === 'simplified' || stored === 'traditional') {
            mode.value = stored
        }
    } catch {
        // 读取失败保持默认 none —— 不虚构服务端状态。
    }
})

async function saveMode(): Promise<void> {
    if (mode.value !== 'none' && mode.value !== 'simplified' && mode.value !== 'traditional') {
        mode.value = 'none'
        return
    }

    savingMode.value = true
    try {
        await settingApi.setSetting(POST_PROCESS_VARIANT_KEY, mode.value)
        toastStore.push({ message: t('common.saved'), type: 'success' })
    } catch {
        toastStore.push({ message: t('common.saveFailed'), type: 'error' })
    } finally {
        savingMode.value = false
    }

    // 模式变化使旧预览失效 —— 计数按新模式重算,执行结果同理清空。
    preview.value = null
    executed.value = null
}

/// 400 的 message 来自服务端(模式 none / 超过批处理上限),如实转达。
function batchErrorMessage(error: unknown): string {
    const data = axios.isAxiosError(error) ? error.response?.data : undefined
    if (data && typeof data === 'object' && 'message' in data && typeof data.message === 'string') {
        return data.message
    }

    return t('settings.postProcess.batchFailed')
}

async function runPreview(): Promise<void> {
    previewing.value = true
    executed.value = null
    try {
        preview.value = await batchApi.preview()
    } catch (error) {
        preview.value = null
        toastStore.push({ message: batchErrorMessage(error), type: 'error' })
    } finally {
        previewing.value = false
    }
}

async function runExecute(): Promise<void> {
    executing.value = true
    try {
        executed.value = await batchApi.execute()
        toastStore.push({
            message: t('settings.postProcess.executed', {
                converted: executed.value.converted,
                skipped: executed.value.skipped,
                failed: executed.value.failed.length
            }),
            type: executed.value.failed.length > 0 ? 'error' : 'success'
        })
    } catch (error) {
        toastStore.push({ message: batchErrorMessage(error), type: 'error' })
    } finally {
        executing.value = false
    }
}
</script>
