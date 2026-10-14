<template>
    <!-- ═══ 设置 · 自动化(新 tab,IA 重构批 2):每日补缺策略卡 + 定时任务下钻行
         (批 3 任务表收编后此行退役)。0 个已配置服务时首用守卫:警示 + 跳翻译
         服务 tab,复用既有 description/servicesLink 文案。 -->
    <div class="space-y-3 px-4">
        <div
            v-if="noProviders"
            class="border-warning/50 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-md border px-4 py-2.5 text-[13px]"
            data-test="automation-no-providers">
            <span class="dot bg-warning flex-none"></span>
            <span class="text-secondary-content min-w-0 flex-1 text-xs leading-snug">
                {{ t('settings.automation.description') }}
                <button
                    type="button"
                    class="text-accent cursor-pointer underline underline-offset-2"
                    data-test="automation-goto-providers"
                    @click="router.push({ name: 'settings-providers' })">
                    {{ t('settings.automation.servicesLink') }}
                </button>
                {{ t('settings.automation.descriptionSuffix') }}
            </span>
        </div>

        <AutomationBatchesCard />

        <!-- 定时任务下钻行:行式样式与系统页一致 -->
        <div
            class="border-line bg-secondary flex flex-wrap items-center gap-x-3 gap-y-2 rounded-md border px-4 py-2.5 text-[13px]"
            data-test="automation-row-schedule">
            <span class="dot bg-success flex-none"></span>
            <b class="flex-none">{{ t('settings.schedule.heading') }}</b>
            <span class="text-secondary-content min-w-0 flex-1 truncate text-xs">
                {{ t('settings.schedule.subLineBase') }}
            </span>
            <span class="ml-auto flex flex-none items-center gap-2">
                <button
                    type="button"
                    class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent ml-auto cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:ring-2 focus-visible:outline-none"
                    data-test="automation-open-schedule"
                    @click="router.push({ name: 'schedule' })">
                    {{ t('settings.schedule.openPanel') }}
                </button>
            </span>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'

const router = useRouter()
const settingStore = useSettingStore()
const { t } = useI18n()

/// 首用守卫:SERVICE_TYPE 是已配置服务数组,空数组 = 自动化无事可做
const noProviders = computed(() => {
    const raw = (settingStore.getSetting(SETTINGS.SERVICE_TYPE) as string) ?? '[]'
    try {
        return (JSON.parse(raw) as string[]).length === 0
    } catch {
        return true
    }
})
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
