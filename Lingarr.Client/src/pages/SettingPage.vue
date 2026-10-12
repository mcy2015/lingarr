<template>
    <div class="flex h-full min-h-0 w-full flex-col gap-4 overflow-hidden p-4 lg:p-6">
        <div class="flex shrink-0 flex-wrap items-center gap-3">
            <TabComponent
                :model-value="activeTab"
                :options="tabs"
                @update:model-value="switchTab" />
            <button
                v-if="settings.getSetting(SETTINGS.AUTH_ENABLED) == 'true'"
                class="border-line text-secondary-content hover:text-primary-content hover:border-accent ml-auto flex cursor-pointer items-center gap-2 rounded-md border px-3 py-1.5 text-sm transition-colors"
                @click="handleLogout">
                <Icon name="logout" class="h-4 w-4" />
                {{ t('settings.menu.logout') }}
            </button>
        </div>

        <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
            <router-view v-slot="{ Component }">
                <transition name="fade" mode="out-in">
                    <component :is="Component" />
                </transition>
            </router-view>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ITabOption, SETTINGS } from '@/types'
import { useSettingStore } from '@/store/setting'
import services from '@/services'
import Icon from '@/components/icons/Icon.vue'
import TabComponent from '@/components/common/TabComponent.vue'

const router = useRouter()
const route = useRoute()
const settings = useSettingStore()
const { t } = useI18n()

const GROUPS = ['providers', 'sources', 'quality', 'system'] as const
type Group = (typeof GROUPS)[number]

// computed so labels re-resolve when the locale changes
const tabs = computed<ITabOption[]>(() =>
    GROUPS.map((group) => ({ label: t(`settings.menu.groups.${group}`), value: group }))
)

const activeTab = ref<Group>(groupFromRoute(route.name))

function groupFromRoute(name: unknown): Group {
    const match = typeof name === 'string' ? name.match(/^settings-(\w+)$/) : null
    return (match && (GROUPS as readonly string[]).includes(match[1]) ? match[1] : 'providers') as Group
}

watch(
    () => route.name,
    (name) => {
        activeTab.value = groupFromRoute(name)
    }
)

const switchTab = (value: string): void => {
    router.push({ name: `settings-${value}` })
}

const handleLogout = async (): Promise<void> => {
    try {
        await services.auth.logout()
        router.push({ name: 'login' })
    } catch (error) {
        console.error('Logout failed:', error)
    }
}
</script>
