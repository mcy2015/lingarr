<template>
    <div class="flex min-h-0 flex-1 flex-col gap-3.5">
        <!-- ═══ 范围条:媒体类型 + 搜索 + 媒体选择 + 预览 ═══ -->
        <div
            class="border-line bg-secondary flex shrink-0 flex-wrap items-center gap-x-3 gap-y-2 rounded-md border px-4 py-3">
            <div class="border-line flex items-center rounded-md border p-0.5" role="group">
                <button
                    v-for="kind in mediaKinds"
                    :key="kind.value"
                    type="button"
                    class="cursor-pointer rounded px-2.5 py-1 text-xs font-medium transition-colors"
                    :class="
                        mediaKind === kind.value
                            ? 'bg-accent text-accent-content'
                            : 'text-secondary-content hover:text-primary-content'
                    "
                    :aria-pressed="mediaKind === kind.value"
                    @click="switchKind(kind.value)">
                    {{ kind.label }}
                </button>
            </div>
            <input
                v-model="mediaQuery"
                type="text"
                class="mono border-line bg-primary text-primary-content placeholder:text-secondary-content/50 w-52 rounded border px-2.5 py-1.5 text-[11.5px] outline-none"
                :placeholder="t('import.normalize.searchPlaceholder')"
                :aria-label="t('import.normalize.searchPlaceholder')" />
            <select
                v-model="selectedMediaId"
                data-test="normalize-media-select"
                class="border-line bg-primary text-primary-content max-w-72 min-w-0 flex-1 cursor-pointer rounded-md border px-2.5 py-1.5 text-xs"
                :aria-label="t('import.normalize.pickMedia')"
                :disabled="mediaOptions.length === 0">
                <option :value="undefined" disabled>
                    {{
                        mediaOptions.length === 0
                            ? t('import.normalize.noMedia')
                            : t('import.normalize.pickMedia')
                    }}
                </option>
                <option v-for="option in mediaOptions" :key="option.id" :value="option.id">
                    {{ option.title }}
                </option>
            </select>
            <ButtonComponent
                size="sm"
                data-test="normalize-preview-btn"
                :loading="previewing"
                :disabled="selectedMediaId === undefined"
                @click="runPreview">
                {{ previewing ? t('import.normalize.previewing') : t('import.normalize.preview') }}
            </ButtonComponent>
        </div>

        <!-- ═══ 错误态:目录只读 / 目录不存在 ═══ -->
        <div
            v-if="preview?.error"
            data-test="normalize-error"
            class="border-danger/45 bg-danger/10 text-danger shrink-0 rounded-md border px-4 py-3 text-[13px]">
            {{ errorText }}
        </div>

        <!-- ═══ 预览面板 ═══ -->
        <div
            v-if="preview && !preview.error"
            class="border-line bg-secondary flex min-h-0 flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('import.normalize.previewTitle') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{
                        t('import.normalize.meta', {
                            items: preview.items.length,
                            skipped: preview.skipped.length
                        })
                    }}
                </span>
            </div>

            <div class="shell-scroll min-h-0 flex-1 overflow-auto">
                <table class="w-full text-[13px]">
                    <thead>
                        <tr>
                            <th :class="thClass">{{ t('import.normalize.table.old') }}</th>
                            <th :class="thClass">{{ t('import.normalize.table.new') }}</th>
                            <th :class="thClass">{{ t('import.normalize.table.language') }}</th>
                            <th :class="thClass">{{ t('import.normalize.table.media') }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-if="preview.items.length === 0" class="hover:bg-tertiary/50">
                            <td :class="tdClass" colspan="4">
                                <span class="text-secondary-content/60 text-xs">
                                    {{
                                        preview.skipped.length === 0
                                            ? t('import.normalize.empty')
                                            : t('import.normalize.nothing')
                                    }}
                                </span>
                            </td>
                        </tr>
                        <tr
                            v-for="item in preview.items"
                            :key="item.oldPath"
                            data-test="normalize-item-row"
                            class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <span class="mono text-xs">{{ item.oldName }}</span>
                            </td>
                            <td :class="tdClass">
                                <span class="mono text-success text-xs">{{ item.newName }}</span>
                            </td>
                            <td :class="tdClass">
                                <span :class="chipClass('ok')">{{ item.language }}</span>
                            </td>
                            <td :class="tdClass">
                                <span class="text-secondary-content text-xs">
                                    {{ item.mediaTitle }}
                                </span>
                            </td>
                        </tr>
                    </tbody>
                </table>

                <!-- 跳过区:每项带原因 -->
                <div v-if="preview.skipped.length > 0" class="border-line border-t px-4 py-3">
                    <b class="text-[12.5px] font-semibold">
                        {{ t('import.normalize.skippedTitle', { count: preview.skipped.length }) }}
                    </b>
                    <ul class="mt-2 flex flex-col gap-1.5">
                        <li
                            v-for="skipped in preview.skipped"
                            :key="skipped.path"
                            data-test="normalize-skip-row"
                            class="flex flex-wrap items-center gap-x-2.5 gap-y-1 text-xs">
                            <span class="mono text-secondary-content min-w-0 break-all">
                                {{ fileNameOf(skipped.path) }}
                            </span>
                            <span :class="chipClass('warn')">{{ reasonText(skipped.reason) }}</span>
                        </li>
                    </ul>
                </div>
            </div>

            <!-- 确认执行条:危险操作,二次确认 -->
            <div class="border-line flex shrink-0 flex-wrap items-center gap-2 border-t px-4 py-3">
                <span v-if="confirming" class="text-warning text-xs">
                    {{ t('import.normalize.confirmHint') }}
                </span>
                <span class="flex-1"></span>
                <ButtonComponent
                    v-if="confirming"
                    variant="secondary"
                    size="sm"
                    :disabled="executing"
                    @click="cancelConfirm">
                    {{ t('import.normalize.cancel') }}
                </ButtonComponent>
                <button
                    v-if="preview.items.length > 0"
                    type="button"
                    data-test="normalize-confirm"
                    class="border-danger/60 bg-danger/10 text-danger hover:bg-danger/20 cursor-pointer rounded-md border px-3 py-1.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50"
                    :disabled="executing"
                    @click="onConfirmClick">
                    {{
                        executing
                            ? t('import.normalize.executing')
                            : confirming
                              ? t('import.normalize.confirmAgain')
                              : t('import.normalize.confirm', { count: preview.items.length })
                    }}
                </button>
            </div>
        </div>

        <!-- ═══ 执行结果 ═══ -->
        <div
            v-if="result"
            data-test="normalize-result"
            class="border-line bg-secondary shrink-0 rounded-md border px-4 py-3">
            <div class="flex flex-wrap items-center gap-2.5 text-[13px]">
                <span class="text-success font-semibold">
                    ✓ {{ t('import.normalize.result', { count: result.renamed }) }}
                </span>
                <span v-if="result.failed.length > 0" class="text-danger font-semibold">
                    ✗ {{ t('import.normalize.resultFailed', { count: result.failed.length }) }}
                </span>
                <ButtonComponent variant="secondary" size="sm" class="ml-auto" @click="runPreview">
                    {{ t('import.normalize.rePreview') }}
                </ButtonComponent>
            </div>
            <ul
                v-if="result.failed.length > 0"
                class="border-line mt-2.5 flex flex-col gap-1.5 border-t pt-2.5">
                <li
                    v-for="failed in result.failed"
                    :key="failed.path"
                    class="flex flex-wrap items-baseline gap-2 text-xs">
                    <span class="mono text-secondary-content min-w-0 break-all">
                        {{ fileNameOf(failed.path) }}
                    </span>
                    <span class="text-danger">{{ failed.error }}</span>
                </li>
            </ul>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import useDebounce from '@/cells/kernel/composables/useDebounce'
import { importService } from '@/cells/import/services/importService'
import { mediaService } from '@/cells/app/api/mediaService'
import { useToastStore } from '@/cells/app/stores/toast'
import {
    IMovie,
    INormalizeExecuteResponse,
    INormalizePreviewResponse,
    IPagedResult,
    IShow,
    INormalizeSkipReason
} from '@/types'

/// F3 归整 tab:对单个电影/剧集的外挂字幕做「预览 diff → 二次确认 → 执行
/// 重命名」。服务端保证纯预览零写入,跳过项全部带机器可读原因。
const { t } = useI18n()
const toastStore = useToastStore()

const mediaApi = mediaService(axios)
const importApi = importService(axios)

type MediaKind = 'Movie' | 'Show'

const mediaKind = ref<MediaKind>('Movie')
const mediaQuery = ref('')
const selectedMediaId = ref<number | undefined>(undefined)
const mediaOptions = ref<{ id: number; title: string }[]>([])

const previewing = ref(false)
const preview = ref<INormalizePreviewResponse | null>(null)
const confirming = ref(false)
const executing = ref(false)
const result = ref<INormalizeExecuteResponse | null>(null)

const mediaKinds = computed(() => [
    { value: 'Movie' as MediaKind, label: t('import.normalize.mediaType.movie') },
    { value: 'Show' as MediaKind, label: t('import.normalize.mediaType.show') }
])

const errorText = computed(() =>
    preview.value?.error === 'readOnly'
        ? t('import.normalize.readOnly')
        : t('import.normalize.missingDirectory')
)

const fileNameOf = (path: string): string => path.split(/[\\/]/).pop() ?? path

const reasonText = (reason: INormalizeSkipReason): string => t(`import.normalize.reason.${reason}`)

const loadMedia = async (): Promise<void> => {
    try {
        const page =
            mediaKind.value === 'Movie'
                ? await mediaApi.movies<IPagedResult<IMovie>>(
                      1,
                      mediaQuery.value.trim(),
                      'title',
                      true
                  )
                : await mediaApi.shows<IPagedResult<IShow>>(
                      1,
                      mediaQuery.value.trim(),
                      'title',
                      true
                  )
        mediaOptions.value = page.items.map((item) => ({ id: item.id, title: item.title }))
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        toastStore.push({
            message: t('import.normalize.mediaLoadFailed', { message }),
            type: 'error'
        })
        mediaOptions.value = []
    }
}

const loadMediaDebounced = useDebounce(() => void loadMedia(), 300)

watch(mediaQuery, () => {
    void loadMediaDebounced()
})

const switchKind = (kind: MediaKind): void => {
    if (mediaKind.value === kind) return
    mediaKind.value = kind
    selectedMediaId.value = undefined
    void loadMedia()
}

onMounted(() => {
    void loadMedia()
})

// switching media invalidates the preview on screen
watch(selectedMediaId, () => {
    preview.value = null
    result.value = null
    confirming.value = false
})

const runPreview = async (): Promise<void> => {
    if (selectedMediaId.value === undefined || previewing.value) return
    const mediaId = Number(selectedMediaId.value)
    if (!Number.isFinite(mediaId)) return
    previewing.value = true
    confirming.value = false
    result.value = null
    try {
        preview.value = await importApi.normalizePreview(mediaKind.value, mediaId)
    } catch (error) {
        preview.value = null
        const message = error instanceof Error ? error.message : String(error)
        toastStore.push({
            message: t('import.normalize.previewFailed', { message }),
            type: 'error'
        })
    } finally {
        previewing.value = false
    }
}

/// Two-step confirm: the first click only arms the action.
const onConfirmClick = (): void => {
    if (!confirming.value) {
        confirming.value = true
        return
    }
    void runExecute()
}

const cancelConfirm = (): void => {
    confirming.value = false
}

const runExecute = async (): Promise<void> => {
    if (!preview.value || preview.value.items.length === 0 || executing.value) return
    const mediaId = Number(selectedMediaId.value)
    if (!Number.isFinite(mediaId)) return
    executing.value = true
    try {
        result.value = await importApi.normalizeExecute({
            mediaType: mediaKind.value,
            mediaId,
            items: preview.value.items.map((item) => ({
                oldPath: item.oldPath,
                newPath: item.newPath,
                language: item.language,
                mediaTitle: item.mediaTitle
            }))
        })
        preview.value = null
        confirming.value = false
        if (result.value.failed.length > 0) {
            toastStore.push({
                message: t('import.normalize.resultFailed', { count: result.value.failed.length }),
                type: 'error'
            })
        }
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        toastStore.push({
            message: t('import.normalize.executeFailed', { message }),
            type: 'error'
        })
    } finally {
        executing.value = false
    }
}

// ── prototype chip vocabulary (same classes as the import workbench) ──
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'
const tdClass = 'border-line/50 align-middle border-b px-3 py-2'

const chipClass = (variant?: 'ok' | 'warn'): string =>
    [
        'mono bg-tertiary border-line text-secondary-content inline-flex items-center gap-1 whitespace-nowrap rounded border px-1.5 py-px text-[11px]',
        variant === 'ok'
            ? 'text-success border-success/40'
            : variant === 'warn'
              ? 'text-warning border-warning/45'
              : ''
    ].join(' ')
</script>
