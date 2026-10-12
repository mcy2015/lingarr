<template>
    <div class="space-y-4">
        <TabComponent v-model="activeTab" :options="tabs" />

        <p v-if="activeTab === 'season'" class="border-line bg-secondary text-secondary-content rounded-md border px-4 py-2 text-sm">
            {{ t('import.seasonHint') }}
        </p>

        <div
            class="border-line bg-secondary hover:border-accent cursor-pointer rounded-md border border-dashed p-8 text-center transition-colors"
            @click="fileInput?.click()"
            @dragover.prevent
            @drop.prevent="onDrop">
            <Icon name="folders" class="text-secondary-content/60 mx-auto h-8 w-8" />
            <p class="text-primary-content mt-2 text-sm">{{ t('import.dropzone.title') }}</p>
            <p class="text-secondary-content/60 mono mt-1 text-xs">{{ t('import.dropzone.formats') }}</p>
            <input
                ref="fileInput"
                type="file"
                class="hidden"
                multiple
                accept=".srt,.ass,.ssa"
                @change="onFileInput" />
        </div>

        <p v-if="files.length === 0" class="text-secondary-content/60 text-sm">{{ t('import.empty') }}</p>

        <div v-else class="border-line bg-secondary overflow-x-auto rounded-md border">
            <table class="w-full text-sm">
                <thead>
                    <tr class="border-line border-b">
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.file') }}</th>
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.season') }}</th>
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.episode') }}</th>
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.match') }}</th>
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.languages') }}</th>
                    </tr>
                </thead>
                <tbody>
                    <tr
                        v-for="row in previews"
                        :key="row.fileName"
                        class="border-line/50 hover:bg-tertiary/50 border-b">
                        <td class="mono px-4 py-2">{{ row.fileName }}</td>
                        <td class="mono px-4 py-2">{{ row.season ?? '—' }}</td>
                        <td class="mono px-4 py-2">{{ row.episode ?? '—' }}</td>
                        <td class="px-4 py-2">
                            <span class="rounded border px-2 py-0.5 text-xs" :class="tierClass(row.matchTier)">
                                {{ t('import.match.' + row.matchTier) }}
                            </span>
                        </td>
                        <td class="px-4 py-2">
                            <span
                                v-for="tag in row.languageTags"
                                :key="tag"
                                class="border-line text-secondary-content mono mr-1 rounded border px-1.5 py-0.5 text-xs">
                                {{ tag }}
                            </span>
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>

        <div class="border-line bg-secondary flex flex-wrap items-center gap-3 rounded-md border px-4 py-3">
            <template v-if="activeTab === 'season'">
                <label class="text-secondary-content flex items-center gap-2 text-sm">
                    {{ t('import.target.show') }}
                    <select
                        v-model="selectedShowId"
                        class="border-line bg-primary text-primary-content rounded-md border px-3 py-1.5 text-sm">
                        <option :value="undefined" disabled>{{ t('import.target.pickShow') }}</option>
                        <option v-for="show in shows" :key="show.id" :value="show.id">{{ show.title }}</option>
                    </select>
                </label>
                <label class="text-secondary-content flex items-center gap-2 text-sm">
                    {{ t('import.target.season') }}
                    <select
                        v-model="selectedSeasonNumber"
                        class="border-line bg-primary text-primary-content rounded-md border px-3 py-1.5 text-sm"
                        :disabled="seasons.length === 0">
                        <option v-for="season in seasons" :key="season" :value="season">S{{ String(season).padStart(2, '0') }}</option>
                    </select>
                </label>
            </template>
            <label class="text-secondary-content flex items-center gap-2 text-sm">
                {{ t('import.options.source') }}
                <select
                    v-model="sourceLanguage"
                    class="border-line bg-primary text-primary-content rounded-md border px-3 py-1.5 text-sm">
                    <option v-for="lang in languages.source" :key="lang.code" :value="lang.code">{{ lang.name }}</option>
                </select>
            </label>
            <label class="text-secondary-content flex items-center gap-2 text-sm">
                {{ t('import.options.target') }}
                <select
                    v-model="targetLanguage"
                    class="border-line bg-primary text-primary-content rounded-md border px-3 py-1.5 text-sm">
                    <option v-for="lang in languages.target" :key="lang.code" :value="lang.code">{{ lang.name }}</option>
                </select>
            </label>
            <label class="text-secondary-content flex items-center gap-2 text-sm">
                {{ t('import.options.translate') }}
                <ToggleButton v-model="translate" />
            </label>
            <div class="grow" />
            <ButtonComponent :disabled="importing || files.length === 0" @click="runImport">
                {{ t('import.actions.import') }}
            </ButtonComponent>
            <ButtonComponent variant="secondary" :disabled="importing" @click="clearFiles">
                {{ t('import.actions.clear') }}
            </ButtonComponent>
        </div>

        <div v-if="results.length > 0" class="border-line bg-secondary overflow-x-auto rounded-md border">
            <table class="w-full text-sm">
                <thead>
                    <tr class="border-line border-b">
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.file') }}</th>
                        <th class="text-secondary-content px-4 py-2 text-left font-medium">{{ t('import.table.status') }}</th>
                    </tr>
                </thead>
                <tbody>
                    <tr v-for="item in results" :key="item.fileName" class="border-line/50 border-b">
                        <td class="mono px-4 py-2">{{ item.fileName }}</td>
                        <td class="px-4 py-2">
                            <span v-if="!item.success" class="text-danger">{{ t('import.status.failed') }} — {{ item.error }}</span>
                            <span v-else-if="item.translationRequestId" class="text-success mono">
                                {{ t('import.status.queued', { id: item.translationRequestId }) }}
                            </span>
                            <span v-else-if="item.mediaBound" class="text-success">{{ t('import.status.bound') }}</span>
                            <span v-else class="text-success">{{ t('import.status.fallback') }}</span>
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TabComponent from '@/components/common/TabComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'
import Icon from '@/components/icons/Icon.vue'
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

const files = ref<File[]>([])
const previews = ref<IParsedSubtitleFilename[]>([])
const results = ref<IImportedSubtitleResult[]>([])
const importing = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

const sourceLanguage = ref('en')
const targetLanguage = ref('')
const translate = ref(false)

// directed season import: show + season binding
const shows = ref<IShow[]>([])
const selectedShowId = ref<number | undefined>(undefined)
const selectedSeasonNumber = ref<number | undefined>(undefined)

const seasons = computed<number[]>(() => {
    const show = shows.value.find((candidate) => candidate.id === selectedShowId.value)
    return show ? show.seasons.map((season) => season.seasonNumber).sort((a, b) => a - b) : []
})

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

const addFiles = (incoming: File[]): void => {
    const supported = incoming.filter((file) => /\.(srt|ass|ssa)$/i.test(file.name))
    const known = new Set(files.value.map((file) => file.name + ':' + file.size))
    const fresh = supported.filter((file) => !known.has(file.name + ':' + file.size))
    files.value.push(...fresh)
    void parsePreviews()
}

const parsePreviews = async (): Promise<void> => {
    if (files.value.length === 0) {
        previews.value = []
        return
    }
    try {
        previews.value = await services.import.parseFilenames(files.value.map((file) => file.name))
    } catch {
        previews.value = []
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
    files.value = []
    previews.value = []
    results.value = []
}

const tierClass = (tier: string): Record<string, boolean> => ({
    'text-success border-success/40': tier === 'Exact',
    'text-accent border-accent/40': tier === 'Guess',
    'text-danger border-danger/40': tier === 'None'
})

const runImport = async (): Promise<void> => {
    importing.value = true
    try {
        const directed = activeTab.value === 'season' && selectedShowId.value && selectedSeasonNumber.value
        const response = await services.import.importFiles(
            files.value,
            sourceLanguage.value,
            targetLanguage.value,
            translate.value,
            directed ? 'Show' : undefined,
            directed ? selectedShowId.value : undefined,
            directed ? selectedSeasonNumber.value : undefined
        )
        results.value = response.results
        const succeeded = response.results.filter((item) => item.success).length
        toastStore.push({ message: t('import.toast.success', { count: succeeded }), type: 'success' })
        clearFiles()
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        toastStore.push({ message: t('import.toast.failed', { message }), type: 'error' })
    } finally {
        importing.value = false
    }
}
</script>
