<template>
    <div class="w-full space-y-3.5">
        <TabComponent v-model="activeTab" :options="tabs" />
        <input
            ref="fileInput"
            type="file"
            class="hidden"
            multiple
            accept=".srt,.ass,.ssa"
            @change="onFileInput" />

        <!-- ═══ 自由导入 ═══ -->
        <div v-if="activeTab === 'free'" class="grid items-start gap-3.5 md:grid-cols-[300px_minmax(0,1fr)]">
            <div>
                <button
                    type="button"
                    class="hover:bg-tertiary/40 w-full cursor-pointer rounded-md px-5 py-8 text-center transition-colors"
                    :style="{
                        borderWidth: '1.5px',
                        borderStyle: 'dashed',
                        borderColor: 'color-mix(in srgb, var(--accent) 55%, var(--line))'
                    }"
                    @click="fileInput?.click()"
                    @dragover.prevent
                    @drop.prevent="onDrop">
                    <b class="text-primary-content text-sm font-semibold">
                        {{ t('import.dropzone.title') }}
                    </b>
                    <span class="mono text-secondary-content mt-2 block text-[11px]">
                        .srt · .ass · .ssa
                        <span class="opacity-50">· .vtt（{{ t('import.planned') }}）</span>
                    </span>
                </button>
                <div class="mt-2.5 flex gap-1.5">
                    <span
                        class="mono text-secondary-content flex min-w-0 flex-1 items-center overflow-hidden whitespace-nowrap rounded border border-line bg-secondary px-2.5 py-1.5 text-[11.5px]"
                        :title="t('import.scan.pathTitle')">
                        config/imports
                    </span>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-1 text-xs disabled:cursor-not-allowed disabled:opacity-50"
                        disabled
                        :title="t('import.scan.pathTitle')">
                        {{ t('import.scan.title') }}
                    </button>
                    <span :class="chipClass('dim')">{{ t('import.planned') }}</span>
                </div>
                <div class="text-secondary-content mt-2.5 rounded-md border border-line bg-secondary px-3.5 py-3 text-xs leading-relaxed">
                    <b class="text-primary-content font-semibold">{{ t('import.hint.title') }}</b>
                    · {{ t('import.hint.body') }}
                </div>
            </div>

            <div class="overflow-x-auto rounded-md border border-line bg-secondary">
                <div class="flex items-center gap-2.5 border-b border-line px-4 py-3">
                    <h2 class="text-[13px] font-semibold">{{ t('import.queue.title') }}</h2>
                    <span class="mono text-secondary-content ml-auto text-xs">
                        {{ t('import.queue.meta', { files: rows.length, translate: translateCount, store: storeCount }) }}
                    </span>
                </div>
                <table class="w-full text-[13px]">
                    <thead>
                        <tr>
                            <th :class="thClass">{{ t('import.table.file') }}</th>
                            <th :class="thClass">{{ t('import.table.detected') }}</th>
                            <th :class="thClass">{{ t('import.table.encoding') }}</th>
                            <th :class="thClass">{{ t('import.table.target') }}</th>
                            <th :class="thClass">{{ t('import.table.action') }}</th>
                            <th :class="thClass">{{ t('import.table.status') }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-if="rows.length === 0" class="hover:bg-tertiary/50">
                            <td :class="tdClass" colspan="6">
                                <span class="text-secondary-content/60 text-xs">{{ t('import.empty') }}</span>
                            </td>
                        </tr>
                        <tr v-for="row in rows" :key="row.fileName" class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <span class="mono text-xs">{{ row.fileName }}</span>
                            </td>
                            <td :class="tdClass">
                                <span :class="chipClass()">
                                    {{ row.inspection?.format ?? '—' }}
                                    <template v-if="row.inspection?.lines">
                                        · {{ t('import.lines', { count: row.inspection.lines }) }}</template
                                    >
                                </span>
                            </td>
                            <td :class="tdClass">
                                <span v-if="row.inspection === null" :class="chipClass('dim')">—</span>
                                <span v-else-if="row.inspection.utf8" :class="chipClass('ok')">UTF-8 ✓</span>
                                <span v-else class="text-warning border-warning/45 mono rounded border px-1.5 py-px text-[11px]">
                                    {{ t('import.encoding.convert') }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <span :class="chipClass()">{{ t('import.targetFormat.original') }}</span>
                            </td>
                            <td :class="tdClass">
                                <select
                                    v-model="row.action"
                                    class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]"
                                    :aria-label="`${row.fileName} · ${t('import.table.action')}`">
                                    <option value="translate">{{ t('import.action.translate') }}</option>
                                    <option value="store">{{ t('import.action.store') }}</option>
                                </select>
                            </td>
                            <td :class="tdClass">
                                <span :class="statusOf(row).cls" :title="statusOf(row).title ?? ''">
                                    {{ statusOf(row).text }}
                                </span>
                            </td>
                        </tr>
                    </tbody>
                </table>
                <div
                    class="flex flex-wrap items-center gap-x-4 gap-y-2.5 border-t border-line px-4 py-3 text-[12.5px]">
                    <span class="text-secondary-content flex items-center gap-2">
                        {{ t('import.options.transcode') }}
                        <span
                            class="border-accent bg-accent relative inline-block h-[17px] w-[30px] rounded-full border"
                            :title="t('import.options.transcodeHint')">
                            <span class="bg-accent-content absolute top-0.5 right-0.5 h-[11px] w-[11px] rounded-full"></span>
                        </span>
                    </span>
                    <label class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.chineseConvert') }}
                        <select
                            v-model="chineseConvert"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option value="none">{{ t('import.chinese.none') }}</option>
                            <option value="simplified">{{ t('import.chinese.simplified') }}</option>
                            <option value="traditional">{{ t('import.chinese.traditional') }}</option>
                        </select>
                    </label>
                    <span
                        class="text-secondary-content flex items-center gap-1.5"
                        :title="t('import.options.bilingualHint')">
                        {{ t('import.options.bilingual') }}
                        <span :class="chipClass(bilingualOn ? 'ok' : 'dim')">
                            {{ bilingualOn ? t('import.on') : t('import.off') }} ·
                            {{ t('import.options.bilingualFollows') }}
                        </span>
                    </span>
                    <span class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.writeback') }}
                        <span :class="chipClass()">{{ t('import.writeback.importsDir') }}</span>
                    </span>
                    <span class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.conflict') }}
                        <span :class="chipClass()">{{ t('import.conflict.suffix') }}</span>
                    </span>
                    <span class="text-secondary-content ml-auto text-xs">
                        {{ t('import.estimate', { files: rows.length, lines: totalLines, translate: translateCount }) }}
                    </span>
                </div>
                <div class="flex flex-wrap items-center gap-2 border-t border-line px-4 py-3">
                    <label class="text-secondary-content flex items-center gap-1.5 text-[12.5px]">
                        {{ t('import.options.source') }}
                        <select
                            v-model="sourceLanguage"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option v-for="lang in languages.source" :key="lang.code" :value="lang.code">
                                {{ lang.name }}
                            </option>
                        </select>
                    </label>
                    <label class="text-secondary-content flex items-center gap-1.5 text-[12.5px]">
                        {{ t('import.options.target') }}
                        <select
                            v-model="targetLanguage"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option v-for="lang in languages.target" :key="lang.code" :value="lang.code">
                                {{ lang.name }}
                            </option>
                        </select>
                    </label>
                    <span class="flex-1"></span>
                    <ButtonComponent
                        variant="secondary"
                        size="sm"
                        :disabled="importing || rows.length === 0"
                        @click="clearFiles">
                        {{ t('import.actions.clear') }}
                    </ButtonComponent>
                    <ButtonComponent
                        size="sm"
                        :loading="importing"
                        :disabled="rows.length === 0"
                        @click="runImport">
                        {{ t('import.actions.importCounts', { files: rows.length, translate: translateCount }) }}
                    </ButtonComponent>
                </div>
            </div>
        </div>

        <!-- ═══ 定向导入 · 整季 ═══ -->
        <div v-else>
            <div class="mb-3.5 flex flex-wrap items-center gap-x-4 gap-y-2">
                <div class="min-w-0">
                    <b class="text-[15px] font-semibold">
                        {{ selectedShow?.title ?? t('import.target.pickShow') }}
                    </b>
                    <div class="mono text-secondary-content mt-0.5 text-[11.5px]">
                        {{
                            directed
                                ? t('import.season.targetMeta', {
                                      season: String(selectedSeasonNumber ?? 0).padStart(2, '0'),
                                      files: rows.length
                                  })
                                : t('import.season.pickMeta')
                        }}
                    </div>
                </div>
                <div class="ml-auto flex items-center gap-2">
                    <span class="text-secondary-content text-xs">{{ t('import.target.change') }}</span>
                    <select
                        v-model="selectedShowId"
                        class="border-line bg-primary text-primary-content cursor-pointer rounded-md border px-2.5 py-1 text-xs"
                        :aria-label="t('import.target.pickShow')">
                        <option :value="undefined" disabled>{{ t('import.target.pickShow') }}</option>
                        <option v-for="show in shows" :key="show.id" :value="show.id">{{ show.title }}</option>
                    </select>
                    <select
                        v-model="selectedSeasonNumber"
                        class="border-line bg-primary text-primary-content cursor-pointer rounded-md border px-2.5 py-1 text-xs disabled:opacity-50"
                        :aria-label="t('import.target.season')"
                        :disabled="seasons.length === 0">
                        <option v-for="season in seasons" :key="season" :value="season">
                            S{{ String(season).padStart(2, '0') }}
                        </option>
                    </select>
                </div>
            </div>

            <button
                type="button"
                class="hover:bg-tertiary/40 mb-3 w-full cursor-pointer rounded-md px-5 py-5 text-center transition-colors"
                :style="{
                    borderWidth: '1.5px',
                    borderStyle: 'dashed',
                    borderColor: 'color-mix(in srgb, var(--accent) 55%, var(--line))'
                }"
                @click="fileInput?.click()"
                @dragover.prevent
                @drop.prevent="onDrop">
                <b class="text-primary-content text-sm font-semibold">
                    {{ t('import.season.dropTitle') }}
                </b>
                <span class="mono text-secondary-content mt-2 block text-[11px]">
                    {{ t('import.season.dropFormats') }}
                </span>
            </button>

            <div class="overflow-x-auto rounded-md border border-line bg-secondary">
                <div class="flex items-center gap-2.5 border-b border-line px-4 py-3">
                    <h2 class="text-[13px] font-semibold">{{ t('import.season.preview') }}</h2>
                    <span class="mono text-secondary-content ml-auto text-xs">
                        {{
                            t('import.season.meta', {
                                files: rows.length,
                                matched: matchedCount,
                                unmatched: rows.length - matchedCount
                            })
                        }}
                    </span>
                </div>
                <table class="w-full text-[13px]">
                    <thead>
                        <tr>
                            <th :class="thClass">{{ t('import.table.file') }}</th>
                            <th :class="thClass">{{ t('import.table.episodeCol') }}</th>
                            <th :class="thClass">{{ t('import.table.match') }}</th>
                            <th :class="thClass">{{ t('import.table.languages') }}</th>
                            <th :class="thClass">{{ t('import.table.action') }}</th>
                            <th :class="thClass">{{ t('import.table.status') }}</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-if="rows.length === 0" class="hover:bg-tertiary/50">
                            <td :class="tdClass" colspan="6">
                                <span class="text-secondary-content/60 text-xs">{{ t('import.empty') }}</span>
                            </td>
                        </tr>
                        <tr v-for="row in rows" :key="row.fileName" class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <span class="mono text-xs">{{ row.fileName }}</span>
                            </td>
                            <td :class="tdClass">
                                <span v-if="row.parse?.episode" class="mono text-xs">
                                    E{{ String(row.parse.episode).padStart(2, '0') }}
                                </span>
                                <span v-else :class="chipClass('danger')">
                                    {{ t('import.match.None') }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <span :class="chipClass(row.parse ? tierVariant(row.parse.matchTier) : 'dim')">
                                    {{ row.parse ? t('import.match.' + row.parse.matchTier) : '—' }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <span v-if="row.parse && row.parse.languageTags.length > 0" class="flex flex-wrap gap-1">
                                    <span v-for="tag in row.parse.languageTags" :key="tag" :class="chipClass()">
                                        {{ tag }}
                                    </span>
                                </span>
                                <span v-else :class="chipClass('dim')">—</span>
                            </td>
                            <td :class="tdClass">
                                <select
                                    v-model="row.action"
                                    class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]"
                                    :aria-label="`${row.fileName} · ${t('import.table.action')}`">
                                    <option value="translate">{{ t('import.action.translateShort') }}</option>
                                    <option value="store">{{ t('import.action.storeShort') }}</option>
                                </select>
                            </td>
                            <td :class="tdClass">
                                <span :class="statusOf(row).cls" :title="statusOf(row).title ?? ''">
                                    {{ statusOf(row).text }}
                                </span>
                            </td>
                        </tr>
                    </tbody>
                </table>
                <div class="flex flex-wrap items-center gap-x-4 gap-y-2.5 border-t border-line px-4 py-3 text-[12.5px]">
                    <label class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.source') }}
                        <select
                            v-model="sourceLanguage"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option v-for="lang in languages.source" :key="lang.code" :value="lang.code">
                                {{ lang.name }}
                            </option>
                        </select>
                    </label>
                    <label class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.target') }}
                        <select
                            v-model="targetLanguage"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option v-for="lang in languages.target" :key="lang.code" :value="lang.code">
                                {{ lang.name }}
                            </option>
                        </select>
                    </label>
                    <label class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.chineseConvert') }}
                        <select
                            v-model="chineseConvert"
                            class="mono bg-tertiary text-secondary-content border-line cursor-pointer rounded border px-1.5 py-0.5 text-[11px]">
                            <option value="none">{{ t('import.chinese.none') }}</option>
                            <option value="simplified">{{ t('import.chinese.simplified') }}</option>
                            <option value="traditional">{{ t('import.chinese.traditional') }}</option>
                        </select>
                    </label>
                    <span class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.writeback') }}
                        <span :class="chipClass()">{{ t('import.writeback.mediaDir') }}</span>
                    </span>
                    <span class="text-secondary-content flex items-center gap-1.5">
                        {{ t('import.options.conflict') }}
                        <span :class="chipClass()">{{ t('import.conflict.suffix') }}</span>
                    </span>
                    <span class="flex-1"></span>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2.5 py-1 text-xs disabled:cursor-not-allowed disabled:opacity-50"
                        :disabled="rows.length === 0 || matchedCount === rows.length"
                        @click="excludeUnmatched">
                        {{ t('import.actions.excludeUnmatched') }}
                    </button>
                    <ButtonComponent
                        variant="secondary"
                        size="sm"
                        :disabled="importing || rows.length === 0"
                        @click="clearFiles">
                        {{ t('import.actions.clear') }}
                    </ButtonComponent>
                    <ButtonComponent
                        size="sm"
                        :loading="importing"
                        :disabled="rows.length === 0"
                        @click="runImport">
                        {{ t('import.actions.importCounts', { files: rows.length, translate: translateCount }) }}
                    </ButtonComponent>
                </div>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TabComponent from '@/components/common/TabComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import services from '@/services'
import { useSettingStore } from '@/store/setting'
import { useToastStore } from '@/store/toast'
import {
    IImportedSubtitleResult,
    IPagedResult,
    IParsedSubtitleFilename,
    IShow,
    ITabOption
} from '@/types'

type RowAction = 'translate' | 'store'

interface ImportRow {
    file: File
    fileName: string
    action: RowAction
    parse: IParsedSubtitleFilename | null
    inspection: { format: string; lines: number | null; utf8: boolean } | null
    result: IImportedSubtitleResult | null
}

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()

const activeTab = ref<'free' | 'season'>('free')
// labels go through t() inside a computed so switching language re-renders
// (setup-time evaluation would freeze the labels — the P2 i18n reactivity lesson)
const tabs = computed<ITabOption[]>(() => [
    { label: t('import.tabs.free'), value: 'free' },
    { label: t('import.tabs.season'), value: 'season' }
])

const rows = ref<ImportRow[]>([])
const importing = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

const sourceLanguage = ref('en')
const targetLanguage = ref('')
const chineseConvert = ref('none')

// directed season import: show + season binding
const shows = ref<IShow[]>([])
const selectedShowId = ref<number | undefined>(undefined)
const selectedSeasonNumber = ref<number | undefined>(undefined)

const seasons = computed<number[]>(() => {
    const show = shows.value.find((candidate) => candidate.id === selectedShowId.value)
    return show ? show.seasons.map((season) => season.seasonNumber).sort((a, b) => a - b) : []
})

const selectedShow = computed(() => shows.value.find((show) => show.id === selectedShowId.value))
const directed = computed(() => selectedShow.value !== undefined && selectedSeasonNumber.value !== undefined)

watch(selectedShowId, () => {
    selectedSeasonNumber.value = seasons.value[0]
})

void services.media
    .shows<IPagedResult<IShow>>(1, '', 'title', true)
    .then((page) => {
        shows.value = page.items
    })
    .catch(() => {
        // media list unavailable (e.g. no *arr connection) - season tab stays usable without binding
    })

const languages = computed(() => {
    const settings = settingStore.getSettings
    const source = (settings.source_languages ?? []) as { code: string; name: string }[]
    const target = (settings.target_languages ?? []) as { code: string; name: string }[]
    return { source, target }
})

if (!targetLanguage.value && languages.value.target.length > 0) {
    targetLanguage.value = languages.value.target[0].code
}

// The write-back stage decides bilingual output from this global setting —
// imports only surface it, they cannot override it (yet).
const bilingualOn = computed(() => {
    const value = settingStore.getSetting('bilingual_subtitle_output')
    return String(value).toLowerCase() === 'true'
})

const translateCount = computed(() => rows.value.filter((row) => row.action === 'translate').length)
const storeCount = computed(() => rows.value.filter((row) => row.action === 'store').length)
const matchedCount = computed(() => rows.value.filter((row) => row.parse?.episode != null).length)
const totalLines = computed(() =>
    rows.value.reduce((sum, row) => sum + (row.inspection?.lines ?? 0), 0)
)

// ── prototype chip vocabulary (mono chip + ok/part/warn/danger/dim variants)
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'
const tdClass = 'border-line/50 align-middle border-b px-3 py-2'

const chipClass = (variant?: 'ok' | 'part' | 'warn' | 'danger' | 'dim'): string =>
    [
        'mono bg-tertiary border-line text-secondary-content inline-flex items-center gap-1 whitespace-nowrap rounded border px-1.5 py-px text-[11px]',
        variant === 'ok'
            ? 'text-success border-success/40'
            : variant === 'part'
              ? 'text-accent border-accent/45'
              : variant === 'warn'
                ? 'text-warning border-warning/45'
                : variant === 'danger'
                  ? 'text-danger border-danger/45'
                  : variant === 'dim'
                    ? 'opacity-55'
                    : ''
    ].join(' ')

const tierVariant = (tier: string): 'ok' | 'part' | 'danger' =>
    tier === 'Exact' ? 'ok' : tier === 'Guess' ? 'part' : 'danger'

const statusOf = (row: ImportRow): { cls: string; text: string; title?: string } => {
    const result = row.result
    if (result?.error) {
        return { cls: 'text-danger text-xs', text: `✗ ${t('import.status.failed')}`, title: result.error }
    }
    if (result?.duplicateOfRequestId) {
        return {
            cls: 'text-warning text-xs',
            text: `△ ${t('import.status.duplicate', { id: result.duplicateOfRequestId })}`
        }
    }
    if (result?.translationRequestId) {
        return {
            cls: 'text-success mono text-xs',
            text: `✓ ${t('import.status.queued', { id: result.translationRequestId })}`
        }
    }
    if (result) {
        return {
            cls: 'text-success text-xs',
            text: `✓ ${result.mediaBound ? t('import.status.bound') : t('import.status.fallback')}`
        }
    }
    if (importing.value) {
        return { cls: 'text-accent text-xs', text: t('import.status.processing') }
    }
    return { cls: 'text-accent text-xs', text: t('import.status.waiting') }
}

// ── file intake ───────────────────────────────────────────────
const addFiles = (incoming: File[]): void => {
    const supported = incoming.filter((file) => /\.(srt|ass|ssa)$/i.test(file.name))
    const known = new Set(rows.value.map((row) => row.fileName + ':' + row.file.size))
    const fresh = supported.filter((file) => !known.has(file.name + ':' + file.size))
    rows.value.push(
        ...fresh.map<ImportRow>((file) => ({
            file,
            fileName: file.name,
            // keep the library's no-auto-queue semantics: store-only unless asked
            action: 'store',
            parse: null,
            inspection: null,
            result: null
        }))
    )
    void refreshMetadata()
}

const refreshMetadata = async (): Promise<void> => {
    if (rows.value.length === 0) return
    try {
        const parsed = await services.import.parseFilenames(rows.value.map((row) => row.fileName))
        const byName = new Map(parsed.map((item) => [item.fileName, item]))
        for (const row of rows.value) {
            row.parse = byName.get(row.fileName) ?? null
        }
    } catch {
        // parsing is a preview nicety — import still works without it
    }
    await Promise.all(rows.value.map((row) => inspectRow(row)))
}

/// Client-side counterpart of the server's strict UTF-8 path: BOM/valid-UTF-8
/// check plus a quick line count, so the encoding column shows real data
/// without shipping file bytes to the parse endpoint.
const inspectRow = async (row: ImportRow): Promise<void> => {
    const format = row.fileName.split('.').pop()?.toUpperCase() ?? '—'
    try {
        const buffer = await row.file.arrayBuffer()
        let text: string
        try {
            text = new TextDecoder('utf-8', { fatal: true }).decode(buffer)
        } catch {
            row.inspection = { format, lines: null, utf8: false }
            return
        }
        const lines =
            format === 'SRT'
                ? text.split(/\n\s*\n/).filter((block) => /\d{1,2}:\d{2}:\d{2}[,.]\d{3}/.test(block)).length
                : text.split(/\r?\n/).filter((line) => line.startsWith('Dialogue:')).length
        row.inspection = { format, lines, utf8: true }
    } catch {
        row.inspection = { format, lines: null, utf8: false }
    }
}

const onFileInput = (event: Event): void => {
    const input = event.target as HTMLInputElement
    if (input.files) addFiles(Array.from(input.files))
    input.value = ''
}

const onDrop = (event: DragEvent): void => {
    if (event.dataTransfer?.files) addFiles(Array.from(event.dataTransfer.files))
}

const clearFiles = (): void => {
    rows.value = []
}

const excludeUnmatched = (): void => {
    rows.value = rows.value.filter((row) => row.parse?.episode != null)
}

// ── import execution ──────────────────────────────────────────
// Per-row actions without an API change: split the batch into a
// translate call and a store-only call, then merge results by file name.
const runImport = async (): Promise<void> => {
    importing.value = true
    try {
        const directedBinding =
            activeTab.value === 'season' && selectedShowId.value && selectedSeasonNumber.value
        const batches: [File[], boolean][] = [
            [rows.value.filter((r) => r.action === 'translate').map((r) => r.file), true],
            [rows.value.filter((r) => r.action === 'store').map((r) => r.file), false]
        ]
        const byName = new Map(rows.value.map((row) => [row.fileName, row]))
        let succeeded = 0
        for (const [batch, translate] of batches) {
            if (batch.length === 0) continue
            const response = await services.import.importFiles(
                batch,
                sourceLanguage.value,
                targetLanguage.value,
                translate,
                directedBinding ? 'Show' : undefined,
                directedBinding ? selectedShowId.value : undefined,
                directedBinding ? selectedSeasonNumber.value : undefined,
                chineseConvert.value
            )
            for (const item of response.results) {
                const row = byName.get(item.fileName)
                if (row) row.result = item
                if (item.success) succeeded++
            }
        }
        toastStore.push({ message: t('import.toast.success', { count: succeeded }), type: 'success' })
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        toastStore.push({ message: t('import.toast.failed', { message }), type: 'error' })
    } finally {
        importing.value = false
    }
}
</script>
