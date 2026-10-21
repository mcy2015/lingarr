<template>
    <!-- ═══ 设置 · 自动化(IA 重构批 3 完整形态):每日补缺策略卡 + 定时任务卡
         (任务表收编,cron 唯一编辑入口)。0 个已配置服务时首用守卫:警示 +
         跳翻译服务 tab,复用既有 description/servicesLink 文案。#jobs 深链
         供旧 /schedule 书签 redirect 定位任务表。 -->
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
        <PostProcessCard />
        <SubtitlePreferenceCard />
        <ScheduleJobsCard />
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { SETTINGS } from '@/types'
import { useSettingStore } from '@/cells/app/stores/setting'
import AutomationBatchesCard from '@/cells/settings/components/AutomationBatchesCard.vue'
import PostProcessCard from '@/cells/settings/components/PostProcessCard.vue'
import SubtitlePreferenceCard from '@/cells/settings/components/SubtitlePreferenceCard.vue'
import ScheduleJobsCard from '@/cells/settings/components/schedule/ScheduleJobsCard.vue'

const router = useRouter()
const route = useRoute()
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

/// 旧 /schedule、/settings/tasks 书签 redirect 到本页 #jobs — 定位任务表。
const scrollToJobs = (): void => {
    if (route.hash === '#jobs') {
        document.getElementById('jobs')?.scrollIntoView({ block: 'start' })
    }
}

onMounted(scrollToJobs)
watch(() => route.hash, scrollToJobs)
</script>

<style scoped>
.dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    display: inline-block;
}
</style>
