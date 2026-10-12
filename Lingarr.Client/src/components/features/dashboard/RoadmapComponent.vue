<template>
    <CardComponent :title="t('dashboard.roadmap.title')" description="">
        <template #description>{{ t('dashboard.roadmap.description') }}</template>
        <template #content>
            <div v-for="(item, index) in roadmapItems" :key="index" class="flex justify-center">
                <div
                    class="mt-2 mr-4 h-4 w-4"
                    :class="{
                        'brightness-50': item.fade
                    }">
                    <component
                        :is="item.icon"
                        v-if="item.icon"
                        class="text-primary-content h-4 w-4" />
                </div>
                <div class="grow">
                    <h3
                        class="text-md text-primary-content font-semibold"
                        :class="{
                            'brightness-50': item.fade
                        }">
                        {{ item.title }}
                    </h3>
                    <p
                        class="text-secondary-content/50 text-sm"
                        :class="{
                            'brightness-50': item.fade
                        }">
                        {{ item.description }}
                    </p>
                    <p
                        class="text-tertiary-content mt-1 text-xs"
                        :class="{
                            'brightness-50': item.fade
                        }">
                        {{ item.date }}
                    </p>
                </div>
                <div class="ml-4 shrink-0">
                    <span
                        class="rounded-full px-2 py-1 text-xs font-medium"
                        :class="[
                            item.status === 'Completed'
                                ? 'bg-success text-success-content'
                                : item.status === 'In Progress'
                                  ? 'bg-info text-info-content'
                                  : 'bg-tertiary text-primary-content',
                            item.fade ? 'brightness-50' : ''
                        ]">
                        {{ t(statusLabels[item.status]) }}
                    </span>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AutomationIcon from '@/components/icons/AutomationIcon.vue'
import NotificationIcon from '@/components/icons/NotificationIcon.vue'
import HistoryIcon from '@/components/icons/HistoryIcon.vue'
import LanguageIcon from '@/components/icons/LanguageIcon.vue'
import CardComponent from '@/components/common/CardComponent.vue'
import IntegrationIcon from '@/components/icons/IntegrationIcon.vue'
import StatisticsIcon from '@/components/icons/StatisticsIcon.vue'
import HomeIcon from '@/components/icons/HomeIcon.vue'

const { t } = useI18n()

const statusLabels: Record<string, string> = {
    Completed: 'dashboard.roadmap.status.completed',
    'In Progress': 'dashboard.roadmap.status.inProgress',
    Planned: 'dashboard.roadmap.status.planned'
}

// computed so titles re-resolve when the locale changes
const roadmapItems = computed(() => [
    {
        icon: IntegrationIcon,
        title: t('dashboard.roadmap.items.aiTranslation.title'),
        description: t('dashboard.roadmap.items.aiTranslation.description'),
        date: '2024',
        status: 'Completed',
        fade: true
    },
    {
        icon: NotificationIcon,
        title: t('dashboard.roadmap.items.enhancedNotifications.title'),
        description: t('dashboard.roadmap.items.enhancedNotifications.description'),
        date: '2024',
        status: 'Completed',
        fade: true
    },
    {
        icon: HistoryIcon,
        title: t('dashboard.roadmap.items.translationHistory.title'),
        description: t('dashboard.roadmap.items.translationHistory.description'),
        date: '2024',
        status: 'Completed',
        fade: true
    },
    {
        icon: AutomationIcon,
        title: t('dashboard.roadmap.items.scheduleControls.title'),
        description: t('dashboard.roadmap.items.scheduleControls.description'),
        date: '2024',
        status: 'Completed',
        fade: true
    },
    {
        icon: LanguageIcon,
        title: t('dashboard.roadmap.items.appLocalization.title'),
        description: t('dashboard.roadmap.items.appLocalization.description'),
        date: '2024',
        status: 'Completed',
        fade: true
    },
    {
        icon: StatisticsIcon,
        title: t('dashboard.roadmap.items.dashboardImprovements.title'),
        description: t('dashboard.roadmap.items.dashboardImprovements.description'),
        date: '2024',
        status: 'In Progress',
        fade: false
    },
    {
        icon: HomeIcon,
        title: t('dashboard.roadmap.items.versionRelease.title'),
        description: t('dashboard.roadmap.items.versionRelease.description'),
        date: '2024 - 2025',
        status: 'Planned',
        fade: false
    }
])
</script>
