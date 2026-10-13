<template>
    <!-- ═══ 回退顺序卡(原型 screen-06 A 组 .fb-row 形态):service_type
         JSON 数组的唯一编辑点 —— ↑↓/移除/添加全部写回数组,首位即默认服务。
         langcap chip 三态只读派生(GET /api/translate/languages/{serviceType})。 -->
    <div class="border-line bg-secondary max-w-[640px] rounded-md border" data-test="fallback-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('settings.fallback.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('settings.fallback.meta') }}
            </span>
        </div>

        <div
            v-for="(member, index) in rows"
            :key="member"
            class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-2.5 text-[13px] last:border-b-0"
            :data-test="`fb-row-${index}`">
            <span class="mono text-secondary-content w-4 flex-none text-[11px]">{{ index + 1 }}</span>
            <span class="dot flex-none" :class="dotFor(member)"></span>
            <b class="min-w-0 truncate">{{ nameFor(member) }}</b>
            <span
                class="mono text-secondary-content rounded-[3px] border border-line bg-tertiary px-1.5 py-px text-[10.5px] flex-none"
                :data-test="`fb-role-${index}`">
                {{ index === 0 ? t('settings.fallback.primary') : t('settings.fallback.fallbackSlot') }}
            </span>
            <!-- langcap chip:三态只读派生,点击仅 toast -->
            <button
                type="button"
                class="mono cursor-pointer rounded-[3px] border px-1.5 py-px text-[10.5px] transition-colors"
                :class="langcapClass(member)"
                :data-test="`fb-langcap-${index}`"
                :title="langcapTitle(member)"
                @click="toastStore.push({ message: t('settings.fallback.langcapReadonly'), type: 'info' })">
                {{ langcapLabel(member) }}
            </button>
            <span class="ml-auto flex flex-none gap-1.5">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2 py-0.5 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="index === 0"
                    :title="t('services.moveUp')"
                    :aria-label="t('services.moveUp')"
                    :data-test="`fb-up-${index}`"
                    @click="move(index, -1)">
                    ↑
                </button>
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2 py-0.5 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="index === rows.length - 1"
                    :title="t('services.moveDown')"
                    :aria-label="t('services.moveDown')"
                    :data-test="`fb-down-${index}`"
                    @click="move(index, 1)">
                    ↓
                </button>
                <button
                    type="button"
                    class="border-danger/50 text-danger hover:bg-danger/10 focus-visible:ring-danger cursor-pointer rounded-md border px-2 py-0.5 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="rows.length <= 1"
                    :title="t('services.removeService')"
                    :aria-label="t('services.removeService')"
                    :data-test="`fb-remove-${index}`"
                    @click="remove(index)">
                    {{ t('services.removeService') }}
                </button>
            </span>
        </div>

        <!-- 末行:添加已配置但未上链的服务 -->
        <button
            type="button"
            class="text-secondary-content hover:text-primary-content w-full cursor-pointer px-4 py-2.5 text-left text-[13px] transition-colors"
            data-test="fb-add"
            @click="addMember">
            {{ t('settings.fallback.add') }}
        </button>
    </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { ITranslationServiceHealth, SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import useServiceChain from '@/cells/settings/composables/useServiceChain'
import { translateService } from '@/cells/app/api/translateService'
import { ServiceLanguageTable } from '@/cells/settings/services/chainCoverage'

const props = defineProps<{
    /// health rows for the dots (the providers grid fetches them already)
    health: ITranslationServiceHealth[]
}>()

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const translateApi = translateService(axios)

// 链派生与页面网格共用 useServiceChain(同一 store 派生 computed,不再各自 parse)
const { chain, offChain } = useServiceChain()
const rows = chain

const healthFor = (member: string) => props.health.find((row) => row.provider === member)

const nameFor = (member: string): string => healthFor(member)?.displayName ?? member

function dotFor(member: string): string {
    const row = healthFor(member)
    if (!row || !row.configured) return 'bg-line'
    if (row.successRate == null || row.successRate >= 0.8) return 'bg-success'
    if (row.successRate >= 0.5) return 'bg-warning'
    return 'bg-danger'
}

// ── langcap 派生(只读)──────────────────────────────────────────
const tables = ref<Record<string, ServiceLanguageTable>>({})

onMounted(fetchTables)

async function fetchTables(): Promise<void> {
    const members = [...new Set(chain.value)]
    if (members.length === 0) {
        tables.value = {}
        return
    }
    const results = await Promise.allSettled(
        members.map((serviceType) => translateApi.getServiceLanguages(serviceType))
    )
    const next: Record<string, ServiceLanguageTable> = {}
    results.forEach((result, index) => {
        if (result.status === 'fulfilled') {
            next[members[index]] = {
                serviceType: result.value.serviceType,
                status: result.value.status,
                languages: result.value.languages
            }
        } else {
            next[members[index]] = { serviceType: members[index], status: 'unknown', languages: [] }
        }
    })
    tables.value = next
}

function langcapLabel(member: string): string {
    const table = tables.value[member]
    if (!table) return t('settings.fallback.langcapUnknown')
    if (table.status === 'unrestricted') return t('settings.fallback.langcapUnrestricted')
    if (table.status === 'restricted') return t('settings.fallback.langcapAll')
    return t('settings.fallback.langcapUnknown')
}

/// title 附带语言表前几个语言码(restricted 且表非空时)
function langcapTitle(member: string): string {
    const table = tables.value[member]
    if (!table) return t('settings.fallback.langcapUnknownTitle')
    if (table.status === 'unrestricted') return t('settings.fallback.langcapUnrestrictedTitle')
    if (table.status === 'restricted') {
        const codes = table.languages.map((language) => language.code)
        const preview =
            codes.length > 0 ? ` · ${codes.slice(0, 5).join(', ')}${codes.length > 5 ? ', …' : ''}` : ''
        return `${t('settings.fallback.langcapAllTitle')}${preview}`
    }
    return t('settings.fallback.langcapUnknownTitle')
}

function langcapClass(member: string): string {
    const table = tables.value[member]
    if (table?.status === 'restricted') {
        return 'border-success/40 bg-success/10 text-success-content'
    }
    if (table?.status === 'unrestricted') {
        return 'border-line bg-tertiary text-secondary-content'
    }
    return 'border-line bg-tertiary text-secondary-content/70'
}

// ── 写回 service_type 数组 ──────────────────────────────────────
async function writeChain(next: string[]): Promise<void> {
    await settingStore.updateSetting(SETTINGS.SERVICE_TYPE, JSON.stringify(next), true)
}

function move(index: number, delta: number): void {
    const target = index + delta
    if (target < 0 || target >= rows.value.length) return
    const next = [...rows.value]
    const moved = next[index]
    next[index] = next[target]
    next[target] = moved
    writeChain(next)
}

function remove(index: number): void {
    if (rows.value.length <= 1) return
    const member = rows.value[index]
    writeChain(rows.value.filter((_, currentIndex) => currentIndex !== index))
    toastStore.push({ message: t('settings.fallback.removed', { service: nameFor(member) }), type: 'success' })
}

/// 候选 = 已配置但不在链上的服务(与页面「＋添加服务」共用 offChain 派生,
/// 此处收紧到 configured);无候选 → toast
function addMember(): void {
    const candidates = offChain(props.health).filter((row) => row.configured)
    if (candidates.length === 0) {
        toastStore.push({ message: t('settings.fallback.allChained'), type: 'info' })
        return
    }
    const candidate = candidates[0]
    writeChain([...rows.value, candidate.provider])
    toastStore.push({ message: t('settings.fallback.added', { service: candidate.displayName }), type: 'success' })
}
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
