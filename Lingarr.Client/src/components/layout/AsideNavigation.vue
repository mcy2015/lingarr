<template>
    <div class="relative">
        <!-- Backdrop -->
        <div
            v-if="isOpen"
            class="bg-opacity-50 fixed inset-0 z-40 bg-black md:hidden"
            @click="isOpen = false"></div>
        <!-- Aside -->
        <aside
            :class="isOpen ? 'translate-x-0' : '-translate-x-full md:translate-x-0'"
            class="border-accent bg-secondary fixed top-0 left-0 z-50 flex h-full w-64 flex-col overflow-hidden border-r transition-transform duration-300 ease-in-out md:sticky">
            <TimesIcon
                class="absolute top-1 right-1 block h-6 w-6 cursor-pointer md:hidden"
                @click="isOpen = false" />
            <div class="flex h-16 items-center justify-center">
                <h1 class="text-xl font-bold">Lingarr</h1>
            </div>
            <!-- Navigation -->
            <nav class="grow overflow-y-auto p-6">
                <ul class="space-y-4">
                    <li v-for="(item, index) in menuItems" :key="index">
                        <router-link
                            :to="{ name: item.route }"
                            class="flex w-full items-center justify-start hover:brightness-150"
                            :class="{ 'brightness-150': isActive(item) }"
                            @click="isOpen = false">
                            <component :is="item.icon" class="mr-2 h-4 w-4" />
                            <div class="relative">
                                {{ t(item.label) }}

                                <span
                                    v-if="item.route == 'translations' && activeRequests > 0"
                                    class="bg-accent text-secondary-content absolute -top-1 -right-4 inline-flex items-center justify-center rounded-full px-1 py-0.5 text-xs leading-none font-bold">
                                    {{ activeRequests }}
                                </span>
                            </div>
                        </router-link>
                    </li>
                </ul>
            </nav>
            <!-- Language switcher -->
            <div class="px-6 pb-4">
                <LanguageSwitcher />
            </div>
            <!-- Version and media section -->
            <div class="pointer-events-none h-64 w-full">
                <img
                    v-if="instanceStore.getPoster"
                    :src="`/api/image/${instanceStore.getPoster}`"
                    class="mask-gradient h-full w-full object-cover"
                    alt="poster" />
                <div
                    v-if="instanceStore.getVersion.currentVersion.length"
                    class="absolute right-0 bottom-0 flex w-full justify-center p-4">
                    <BadgeComponent
                        v-if="instanceStore.getVersion.isDevelopment"
                        classes="text-white border-amber-200 bg-amber-500/50">
                        {{ t('version.development') }}
                    </BadgeComponent>
                    <BadgeComponent
                        v-else-if="instanceStore.getVersion.newVersion"
                        classes="text-white border-green-200 bg-green-500/50">
                        {{
                            t('version.updateAvailable', {
                                version: instanceStore.getVersion.latestVersion
                            })
                        }}
                    </BadgeComponent>
                    <BadgeComponent v-else>
                        {{
                            t('version.current', {
                                version: instanceStore.getVersion.currentVersion
                            })
                        }}
                    </BadgeComponent>
                </div>
            </div>
        </aside>
    </div>
</template>

<script setup lang="ts">
import { computed, ComputedRef } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useInstanceStore } from '@/store/instance'
import { useTranslationRequestStore } from '@/store/translationRequest'
import { MenuItem } from '@/ts'
import HomeIcon from '@/components/icons/HomeIcon.vue'
import MovieIcon from '@/components/icons/MovieIcon.vue'
import ShowIcon from '@/components/icons/ShowIcon.vue'
import SettingIcon from '@/components/icons/SettingIcon.vue'
import TimesIcon from '@/components/icons/TimesIcon.vue'
import BadgeComponent from '@/components/common/BadgeComponent.vue'
import LanguageSwitcher from '@/components/common/LanguageSwitcher.vue'
import LanguageIcon from '@/components/icons/LanguageIcon.vue'

const translationRequestStore = useTranslationRequestStore()
const instanceStore = useInstanceStore()
const router = useRouter()
const route = useRoute()
const { t } = useI18n()

const activeRequests: ComputedRef<number> = computed(
    () => translationRequestStore.getActiveTranslationCount
)

const isOpen = computed({
    get: () => instanceStore.getIsOpen,
    set: (value) => instanceStore.setIsOpen(value)
})

const menuItems: MenuItem[] = [
    { label: 'nav.dashboard', icon: HomeIcon, route: 'dashboard', children: [] },
    { label: 'nav.movies', icon: MovieIcon, route: 'movies', children: [] },
    { label: 'nav.shows', icon: ShowIcon, route: 'shows', children: [] },
    { label: 'nav.translations', icon: LanguageIcon, route: 'translations', children: [] },
    {
        label: 'nav.settings',
        icon: SettingIcon,
        route: 'integration-settings',
        children:
            router
                .getRoutes()
                .find((r) => r.name === 'settings')
                ?.children.map((c) => c.name as string) ?? []
    }
]

const isActive = (item: MenuItem) => {
    if (item.route == route.name) return true

    if (item.children?.length) {
        return item.children.includes(route.name as string)
    }
    return false
}
</script>
