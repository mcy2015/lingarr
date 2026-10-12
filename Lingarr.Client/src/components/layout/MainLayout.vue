<template>
    <div
        :class="[`${instanceStore.getTheme}`, 'bg-primary text-primary-content flex min-h-screen']">
        <AsideNavigation />

        <div class="flex w-full flex-col">
            <!-- Header -->
            <header class="border-line bg-secondary px-4 py-2.5 sm:px-6 lg:px-8">
                <div class="flex w-full items-center justify-between space-x-2 md:justify-end">
                    <div class="md:hidden">
                        <MenuIcon class="block h-5 w-5 cursor-pointer" @click="isOpen = !isOpen" />
                    </div>
                    <div class="flex items-center justify-between">
                        <LanguageDropdown />
                        <DropdownComponent width="medium">
                            <template #button>
                                <ThemeIcon class="h-5 w-5" />
                            </template>
                            <template #content>
                                <div
                                    class="py-1"
                                    aria-orientation="vertical"
                                    role="menu"
                                    aria-labelledby="options-menu">
                                    <button
                                        v-for="theme in Object.values(THEMES)"
                                        :key="theme"
                                        class="hover:bg-tertiary text-secondary-content block w-full cursor-pointer px-4 py-2 text-left text-sm capitalize"
                                        @click="setTheme(theme)">
                                        {{ theme }}
                                    </button>
                                </div>
                            </template>
                        </DropdownComponent>
                    </div>
                </div>
            </header>
            <!-- Main Content -->
            <main class="flex-1">
                <router-view v-slot="{ Component }">
                    <transition name="fade" mode="out-in">
                        <component :is="Component" />
                    </transition>
                </router-view>
            </main>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed, onUnmounted } from 'vue'
import { useSignalR } from '@/composables/useSignalR'
import { Hub, IActiveTranslation, ISettings, ITheme, THEMES } from '@/ts'
import { useSettingStore } from '@/store/setting'
import { useInstanceStore } from '@/store/instance'
import useTranslationRequestStore from '@/store/translationRequest'

import AsideNavigation from '@/components/layout/AsideNavigation.vue'
import DropdownComponent from '@/components/common/DropdownComponent.vue'
import LanguageDropdown from '@/components/common/LanguageDropdown.vue'
import ThemeIcon from '@/components/icons/ThemeIcon.vue'
import MenuIcon from '@/components/icons/MenuIcon.vue'

const settingStore = useSettingStore()
const instanceStore = useInstanceStore()
const translationRequestStore = useTranslationRequestStore()
const signalR = useSignalR()
const settingHubConnection = ref<Hub>()
const requestHubConnection = ref<Hub>()

const onSettingUpdate = (setting: { key: keyof ISettings; value: string }) => {
    settingStore.storeSetting(setting.key, setting.value)
}

const onActiveTranslations = (activeTranslations: IActiveTranslation[]) => {
    translationRequestStore.setActiveTranslations(activeTranslations)
}

const setTheme = (theme: ITheme) => {
    instanceStore.storeTheme(theme)
}

const isOpen = computed({
    get: () => instanceStore.getIsOpen,
    set: (value: boolean) => instanceStore.setIsOpen(value)
})

onMounted(async () => {
    await settingStore.applySettingsOnLoad()
    await instanceStore.applyVersionOnLoad()
    await translationRequestStore.fetchActiveTranslations()

    settingHubConnection.value = await signalR.connect('SettingUpdates', '/signalr/SettingUpdates')
    await settingHubConnection.value.joinGroup({ group: 'SettingUpdates' })
    settingHubConnection.value.on('SettingUpdate', onSettingUpdate)

    requestHubConnection.value = await signalR.connect(
        'TranslationRequests',
        '/signalr/TranslationRequests'
    )
    await requestHubConnection.value.joinGroup({ group: 'TranslationRequests' })
    requestHubConnection.value.on('ActiveTranslations', onActiveTranslations)
})

onUnmounted(async () => {
    settingHubConnection.value?.off('SettingUpdate', onSettingUpdate)
    requestHubConnection.value?.off('ActiveTranslations', onActiveTranslations)
})
</script>
