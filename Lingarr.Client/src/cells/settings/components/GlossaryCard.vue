<template>
    <!-- 术语表卡（原型屏 6 · 字幕与质量）：CRUD 直连 /api/glossary -->
    <div class="border-line bg-secondary rounded-md border">
        <div class="border-line flex items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.glossary.title') }}</h2>
            <span class="chip">{{ t('settings.glossary.count', { count: entries.length }) }}</span>
            <span class="flex-1"></span>
            <!-- CSV 导入(原型 screen-06 C):隐藏 file input,真解析真提交 -->
            <input
                ref="csvInput"
                type="file"
                accept=".csv,text/csv"
                class="hidden"
                data-test="glossary-csv-input"
                @change="onCsvPicked" />
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="glossary-import"
                @click="csvInput?.click()">
                {{ t('settings.glossary.import') }}
            </button>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="startCreate">
                {{ t('settings.glossary.add') }}
            </button>
        </div>

        <div v-if="entries.length === 0 && !editing" class="text-secondary-content/60 px-4 py-5 text-center text-xs">
            {{ t('settings.glossary.empty') }}
        </div>

        <div
            v-for="entry in entries"
            :key="entry.id"
            class="border-line flex items-center gap-3 border-b px-4 py-2.5 text-[13px] last:border-b-0">
            <template v-if="editing?.id === entry.id">
                <input
                    v-model="editing.sourceTerm"
                    type="text"
                    class="border-line bg-primary text-primary-content w-40 rounded border px-2 py-1 text-xs outline-none"
                    :aria-label="t('settings.glossary.source')" />
                <span class="text-secondary-content">→</span>
                <input
                    v-model="editing.targetTerm"
                    type="text"
                    class="border-line bg-primary text-primary-content w-40 rounded border px-2 py-1 text-xs outline-none"
                    :aria-label="t('settings.glossary.target')" />
                <label class="text-secondary-content flex items-center gap-1.5 text-xs">
                    <input v-model="editing.isLocked" type="checkbox" class="accent-accent cursor-pointer" />
                    {{ t('settings.glossary.locked') }}
                </label>
                <span class="flex-1"></span>
                <button
                    type="button"
                    class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-2.5 py-1 text-xs font-semibold transition-colors"
                    @click="saveEdit">
                    {{ t('common.saved') }}
                </button>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    @click="editing = null">
                    {{ t('common.close') }}
                </button>
            </template>
            <template v-else>
                <b class="mono text-xs font-semibold">{{ entry.sourceTerm }}</b>
                <span class="text-secondary-content text-xs">
                    → {{ entry.targetTerm }}
                    · {{ entry.isLocked ? t('settings.glossary.locked') : t('settings.glossary.hint') }}
                </span>
                <span class="flex-1"></span>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    @click="startEdit(entry)">
                    {{ t('library.action.detail') }}
                </button>
                <button
                    type="button"
                    class="border-danger/50 text-danger hover:bg-danger/10 cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                    @click="remove(entry)">
                    {{ t('settings.glossary.delete') }}
                </button>
            </template>
        </div>

        <div v-if="editing?.id === 0" class="border-line flex items-center gap-3 border-t px-4 py-2.5 text-[13px]">
            <input
                v-model="editing.sourceTerm"
                type="text"
                class="border-line bg-primary text-primary-content w-40 rounded border px-2 py-1 text-xs outline-none"
                :placeholder="t('settings.glossary.source')"
                :aria-label="t('settings.glossary.source')" />
            <span class="text-secondary-content">→</span>
            <input
                v-model="editing.targetTerm"
                type="text"
                class="border-line bg-primary text-primary-content w-40 rounded border px-2 py-1 text-xs outline-none"
                :placeholder="t('settings.glossary.target')"
                :aria-label="t('settings.glossary.target')" />
            <label class="text-secondary-content flex items-center gap-1.5 text-xs">
                <input v-model="editing.isLocked" type="checkbox" class="accent-accent cursor-pointer" />
                {{ t('settings.glossary.locked') }}
            </label>
            <span class="flex-1"></span>
            <button
                type="button"
                class="bg-accent text-accent-content hover:bg-accent/90 cursor-pointer rounded-md px-2.5 py-1 text-xs font-semibold transition-colors"
                @click="saveEdit">
                {{ t('settings.glossary.add') }}
            </button>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="editing = null">
                {{ t('common.close') }}
            </button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { glossaryService } from '@/cells/app/api/glossaryService'
import { useToastStore } from '@/cells/app/stores/toast'
import { parseGlossaryCsv } from '@/cells/settings/services/glossaryCsv'
import { ITranslationGlossary } from '@/types'

const { t } = useI18n()
const toastStore = useToastStore()
const glossaryApi = glossaryService(axios)

const entries = ref<ITranslationGlossary[]>([])
const editing = ref<Pick<ITranslationGlossary, 'id' | 'sourceTerm' | 'targetTerm' | 'isLocked'> | null>(null)
const csvInput = ref<HTMLInputElement | null>(null)

onMounted(refresh)

async function refresh(): Promise<void> {
    entries.value = await glossaryApi.getAll()
}

/// CSV 导入:解析(畸形行跳过计数)→ bulk 提交 → toast 汇总 → 刷新列表。
async function onCsvPicked(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement
    const file = input.files?.[0]
    // reset so picking the same file again re-fires change
    input.value = ''
    if (!file) return

    let parsed
    try {
        parsed = parseGlossaryCsv(await file.text())
    } catch {
        toastStore.push({ message: t('settings.glossary.importFailed'), type: 'error' })
        return
    }

    if (parsed.entries.length === 0) {
        toastStore.push({
            message: t('settings.glossary.importResult', { imported: 0, skipped: parsed.skipped }),
            type: 'info'
        })
        return
    }

    try {
        const result = await glossaryApi.bulkImport(parsed.entries)
        toastStore.push({
            message: t('settings.glossary.importResult', {
                imported: result.imported,
                skipped: parsed.skipped + result.skipped
            }),
            type: 'success'
        })
        await refresh()
    } catch {
        toastStore.push({ message: t('settings.glossary.importFailed'), type: 'error' })
    }
}

function startCreate(): void {
    editing.value = { id: 0, sourceTerm: '', targetTerm: '', isLocked: false }
}

function startEdit(entry: ITranslationGlossary): void {
    editing.value = {
        id: entry.id,
        sourceTerm: entry.sourceTerm,
        targetTerm: entry.targetTerm,
        isLocked: entry.isLocked
    }
}

async function saveEdit(): Promise<void> {
    const draft = editing.value
    if (!draft || !draft.sourceTerm.trim() || !draft.targetTerm.trim()) return
    try {
        if (draft.id === 0) {
            await glossaryApi.create({
                sourceTerm: draft.sourceTerm.trim(),
                targetTerm: draft.targetTerm.trim(),
                isLocked: draft.isLocked
            })
        } else {
            await glossaryApi.update(draft.id, {
                sourceTerm: draft.sourceTerm.trim(),
                targetTerm: draft.targetTerm.trim(),
                isLocked: draft.isLocked
            })
        }
        editing.value = null
        await refresh()
    } catch {
        toastStore.push({ message: t('settings.glossary.saveFailed'), type: 'error' })
    }
}

async function remove(entry: ITranslationGlossary): Promise<void> {
    await glossaryApi.remove(entry.id)
    await refresh()
}
</script>

<style scoped>
.chip {
    font-family: var(--font-mono, monospace);
    font-size: 11px;
    background: var(--tertiary);
    border: 1px solid var(--line);
    border-radius: 3px;
    padding: 0 7px;
    color: var(--secondary-content);
}
</style>
