<template>
    <div class="grid h-full grid-cols-[auto_1fr]">
        <aside class="bg-secondary w-[3.175rem] shrink-0 md:sticky md:top-0 md:h-dvh md:w-40">
            <nav class="flex h-full flex-col pt-4 md:pt-8 md:pl-4">
                <ul class="flex flex-col space-y-4">
                    <li
                        v-for="(item, index) in menuItems"
                        :key="index"
                        :class="[
                            'w-full hover:brightness-150',
                            { 'brightness-150': $route.name === item.route }
                        ]">
                        <router-link
                            :to="{ name: item.route }"
                            :title="item.label"
                            :aria-label="item.label"
                            class="flex w-full cursor-pointer items-center justify-center md:justify-start">
                            <Icon :name="item.icon" class="h-4 w-4 md:mr-3" />
                            <span class="hidden text-sm md:inline-block">
                                {{ item.label }}
                            </span>
                        </router-link>
                    </li>
                    <li
                        v-if="settings.getSetting(SETTINGS.AUTH_ENABLED) == 'true'"
                        class="text-primary-content/50 w-full hover:brightness-150"
                        @click="handleLogout">
                        <div
                            class="flex w-full cursor-pointer items-center justify-center md:justify-start">
                            <Icon name="logout" class="h-4 w-4 md:mr-3" />
                            <span class="hidden text-sm md:inline-block">
                                {{ t('settings.menu.logout') }}
                            </span>
                        </div>
                    </li>
                </ul>
            </nav>
        </aside>

        <main class="flex">
            <router-view v-slot="{ Component }">
                <transition name="fade" mode="out-in">
                    <component :is="Component" />
                </transition>
            </router-view>
        </main>
    </div>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { computed } from 'vue'
import { MenuItem, SETTINGS } from '@/types'
import { useSettingStore } from '@/store/setting'
import services from '@/services'
import Icon from '@/components/icons/Icon.vue'

const router = useRouter()
const settings = useSettingStore()
const { t } = useI18n()

// computed so labels re-resolve when the locale changes
const menuItems = computed<MenuItem[]>(() => [
    {
        label: t('settings.menu.integrations'),
        icon: 'integration',
        route: 'integration-settings',
        children: []
    },
    {
        label: t('settings.menu.authentication'),
        icon: 'key',
        route: 'authentication-settings',
        children: []
    },
    {
        label: t('settings.menu.services'),
        icon: 'setting',
        route: 'services-settings',
        children: []
    },
    {
        label: t('settings.menu.subtitle'),
        icon: 'language',
        route: 'subtitle-settings',
        children: []
    },
    {
        label: t('settings.menu.automation'),
        icon: 'automation',
        route: 'automation-settings',
        children: []
    },
    {
        label: t('settings.menu.plugins'),
        icon: 'setting',
        route: 'plugins-settings',
        children: []
    },
    { label: t('settings.menu.tasks'), icon: 'task', route: 'tasks-settings', children: [] },
    { label: t('settings.menu.logs'), icon: 'log', route: 'logs-settings', children: [] },
    {
        label: t('settings.menu.telemetry'),
        icon: 'telemetry',
        route: 'telemetry-settings',
        children: []
    }
])

const handleLogout = async () => {
    try {
        await services.auth.logout()
        router.push({ name: 'login' })
    } catch (error) {
        console.error('Logout failed:', error)
    }
}
</script>
