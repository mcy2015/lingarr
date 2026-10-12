<template>
    <div
        :class="[`${instanceStore.getTheme}`, 'bg-primary text-primary-content flex h-dvh overflow-hidden']">
        <a
            href="#main-content"
            class="bg-secondary text-primary-content sr-only fixed top-2 left-2 z-[70] rounded-md px-4 py-2 text-sm font-semibold focus:not-sr-only">
            {{ t('common.skipToContent') }}
        </a>
        <AsideNavigation />

        <div class="flex h-full w-full min-w-0 flex-col">
            <!-- Header: page title + subtitle (prototype topbar) -->
            <header class="border-line bg-secondary flex items-baseline gap-3.5 px-4 py-3.5 sm:px-6 lg:px-8">
                <button
                    type="button"
                    class="focus-visible:ring-accent mr-1 cursor-pointer self-center rounded-md focus-visible:ring-2 focus-visible:outline-none md:hidden"
                    :aria-label="t('common.menu')"
                    @click="isOpen = !isOpen">
                    <Icon name="menu" class="block h-5 w-5" />
                </button>
                <div class="min-w-0">
                    <h1 class="truncate text-[19px] font-bold tracking-tight">{{ pageTitle }}</h1>
                    <span class="text-secondary-content hidden text-[12.5px] sm:block">
                        {{ pageSub }}
                    </span>
                </div>
                <div class="ml-auto flex items-center justify-between">
                    <ButtonComponent
                        v-if="showImportCta"
                        size="sm"
                        class="mr-2"
                        @click="router.push({ name: 'import' })">
                        {{ t('library.toolbar.import') }}
                    </ButtonComponent>
                    <LanguageDropdown />
                    <DropdownComponent ref="themeDropdownEl" width="medium" :label="t('common.theme')">
                        <template #button>
                            <Icon name="theme" class="h-5 w-5" />
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
            </header>
            <!-- Main Content -->
            <main id="main-content" class="min-h-0 flex-1 overflow-y-auto">
                <router-view v-slot="{ Component }">
                    <transition name="fade" mode="out-in">
                        <component :is="Component" />
                    </transition>
                </router-view>
            </main>
        </div>

        <ToastNotification />
    </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useSignalR } from '@/composables/useSignalR'
import { Hub, IActiveTranslation, ISettings, ITheme, THEMES } from '@/types'
import { useSettingStore } from '@/store/setting'
import { useInstanceStore } from '@/store/instance'
import useTranslationRequestStore from '@/store/translationRequest'

import AsideNavigation from '@/components/layout/AsideNavigation.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import DropdownComponent from '@/components/common/DropdownComponent.vue'
import LanguageDropdown from '@/components/common/LanguageDropdown.vue'
import ToastNotification from '@/components/common/ToastNotification.vue'
import Icon from '@/components/icons/Icon.vue'

const settingStore = useSettingStore()
const instanceStore = useInstanceStore()
const translationRequestStore = useTranslationRequestStore()
const signalR = useSignalR()
const router = useRouter()
const route = useRoute()
const { t } = useI18n()
const themeDropdownEl = ref<InstanceType<typeof DropdownComponent>>()
const settingHubConnection = ref<Hub>()
const requestHubConnection = ref<Hub>()

const onSettingUpdate = (setting: { key: keyof ISettings; value: string }) => {
    settingStore.storeSetting(setting.key, setting.value)
}

const onActiveTranslations = (activeTranslations: IActiveTranslation[]) => {
    translationRequestStore.setActiveTranslations(activeTranslations)
}

/// Prototype topbar: page title + one-line context per screen.
const pageMeta: Record<string, { title: string; sub: string; cta?: boolean }> = {
    dashboard: { title: t('nav.dashboard'), sub: t('topbar.sub.dashboard'), cta: true },
    library: { title: t('nav.library'), sub: t('topbar.sub.library'), cta: true },
    queue: { title: t('nav.queue'), sub: t('topbar.sub.queue') },
    'queue-detail': { title: t('nav.queue'), sub: t('topbar.sub.queue') },
    import: { title: t('nav.import'), sub: t('topbar.sub.import') },
    proofread: { title: t('nav.proofread'), sub: t('topbar.sub.proofread') },
    settings: { title: t('nav.settings'), sub: t('topbar.sub.settings') }
}
const currentMeta = computed(() => {
    const name = String(route.name ?? '')
    return (
        pageMeta[name] ??
        (name.startsWith('settings-')
            ? pageMeta.settings
            : { title: 'Lingarr', sub: '' })
    )
})
const pageTitle = computed(() => currentMeta.value.title)
const pageSub = computed(() => currentMeta.value.sub)
const showImportCta = computed(() => currentMeta.value.cta === true)

const setTheme = (theme: ITheme) => {
    instanceStore.storeTheme(theme)
    themeDropdownEl.value?.close()
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
