<template>
    <!-- ═══ 设置 · 系统(原型 screen-06 D):日志与定时任务已提升为下钻屏
         (screens 11/12),此处保留行级入口;认证表单原样内嵌。 -->
    <div class="space-y-2">
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.authentication') }}
        </h2>
        <AuthenticationPage />
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.tasks') }}
        </h2>
        <div
            class="border-line bg-secondary mx-4 flex items-center justify-between gap-3 rounded-md border px-4 py-3">
            <span class="text-sm font-semibold">{{ t('settings.schedule.heading') }}</span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="system-open-schedule"
                @click="router.push({ name: 'schedule' })">
                {{ t('settings.schedule.openPanel') }}
            </button>
        </div>
        <h2 class="text-secondary-content/70 px-4 pt-4 text-xs font-semibold tracking-wider uppercase">
            {{ t('settings.menu.logs') }}
        </h2>
        <div
            class="border-line bg-secondary mx-4 flex items-center justify-between gap-3 rounded-md border px-4 py-3">
            <span class="text-sm font-semibold">{{ t('settings.logs.heading') }}</span>
            <button
                type="button"
                class="border-line text-primary-content hover:bg-tertiary focus-visible:ring-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition-colors focus-visible:ring-2 focus-visible:outline-none"
                data-test="system-open-logs"
                @click="router.push({ name: 'logs' })">
                {{ t('settings.logs.open') }}
            </button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AuthenticationPage from './AuthenticationPage.vue'

const { t } = useI18n()
const router = useRouter()
</script>
