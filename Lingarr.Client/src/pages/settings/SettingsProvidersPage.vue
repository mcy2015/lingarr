<template>
    <div class="space-y-5">
        <!-- ═══ Provider 卡片网格（原型屏 6） ═══ -->
        <p class="text-secondary-content text-[12.5px]">
            {{ t('settings.providers.blurb') }}
        </p>
        <div class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-3">
            <div
                v-for="card in cards"
                :key="card.provider"
                class="rounded-md border border-line bg-secondary flex flex-col gap-2.5 p-4"
                :class="{ 'opacity-55': !card.configured }">
                <div class="flex items-center gap-2.5">
                    <span class="dot" :class="dotClass(card)"></span>
                    <b class="text-[14px] font-semibold">{{ card.displayName }}</b>
                    <span
                        v-if="isDefault(card.provider)"
                        class="bg-accent text-accent-content mono rounded-[3px] px-1.5 py-px text-[10.5px] font-bold">
                        {{ t('settings.providers.default') }}
                    </span>
                    <span class="text-secondary-content ml-auto flex items-center gap-1.5 text-[11.5px]">
                        {{ statusText(card) }}
                    </span>
                </div>
                <div v-if="card.lastError" class="text-danger truncate text-xs" :title="card.lastError">
                    {{ card.lastError }}
                </div>
                <div class="text-secondary-content flex flex-wrap gap-x-5 gap-y-1 text-xs">
                    <span>
                        {{ t('settings.providers.month') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.monthlyCost != null ? formatCost(card.monthlyCost) : '—' }}
                        </b>
                    </span>
                    <span>
                        {{ t('settings.providers.latency') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.latencyMs != null ? `${Math.round(card.latencyMs)} ms` : '—' }}
                        </b>
                    </span>
                    <span>
                        {{ t('settings.providers.success') }}
                        <b class="mono text-primary-content font-semibold">
                            {{ card.successRate != null ? `${Math.round(card.successRate * 100)}%` : '—' }}
                        </b>
                    </span>
                </div>
                <div class="mt-auto flex gap-1.5">
                    <button
                        v-if="!isDefault(card.provider) && card.configured"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        @click="setDefault(card.provider)">
                        {{ t('settings.providers.setDefault') }}
                    </button>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                        :aria-expanded="expanded"
                        @click="expanded = !expanded">
                        {{ t('library.action.detail') }}
                    </button>
                </div>
            </div>
        </div>

        <!-- ═══ 完整配置表单（保留功能，折叠收纳） ═══ -->
        <div v-if="expanded" class="space-y-2">
            <h2 class="text-secondary-content/70 px-4 pt-2 text-xs font-semibold tracking-wider uppercase">
                {{ t('settings.menu.services') }}
            </h2>
            <ServicesPage />
            <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
                {{ t('settings.menu.plugins') }}
            </h2>
            <PluginsPage />
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import ServicesPage from './ServicesPage.vue'
import PluginsPage from './PluginsPage.vue'
import services from '@/services'
import { useSettingStore } from '@/store/setting'
import { ITranslationServiceHealth, SETTINGS } from '@/types'

const { t, locale } = useI18n()
const settingStore = useSettingStore()

const health = ref<ITranslationServiceHealth[]>([])
const expanded = ref(false)

const cards = computed(() =>
    [...health.value].sort((a, b) => Number(b.configured) - Number(a.configured) || a.displayName.localeCompare(b.displayName))
)

const defaultProvider = computed(() => settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string)

onMounted(async () => {
    try {
        health.value = await services.translate.getHealth()
    } catch {
        // the forms below still work without the health glance
    }
})

function isDefault(provider: string): boolean {
    return defaultProvider.value === provider
}

function dotClass(card: ITranslationServiceHealth): string {
    if (!card.configured) return 'bg-line'
    if (card.successRate == null) return 'bg-success'
    if (card.successRate >= 0.8) return 'bg-success'
    if (card.successRate >= 0.5) return 'bg-warning'
    return 'bg-danger'
}

function statusText(card: ITranslationServiceHealth): string {
    if (!card.configured) return t('overview.health.offline')
    if (card.successRate == null) return t('settings.providers.idle')
    if (card.successRate >= 0.8) return t('settings.providers.online')
    if (card.successRate >= 0.5) return t('settings.providers.degraded')
    return t('settings.providers.failing')
}

async function setDefault(provider: string): Promise<void> {
    await settingStore.updateSetting(SETTINGS.SERVICE_TYPE, provider, true)
}

function formatCost(value: number): string {
    return new Intl.NumberFormat(locale.value, {
        style: 'currency',
        currency: 'USD',
        maximumFractionDigits: 2
    }).format(value)
}
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
    flex: none;
}
</style>
