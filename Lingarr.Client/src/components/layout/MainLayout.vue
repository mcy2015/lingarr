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
            <!-- Header (prototype .topbar): h1 + sub on one baseline,
                 ⌘K search and the screen's own CTA on the right. -->
            <header class="border-line bg-secondary flex flex-none items-baseline gap-3.5 px-5 pt-4 pb-3 sm:px-6 lg:px-[26px]">
                <button
                    type="button"
                    class="focus-visible:ring-accent mr-1 cursor-pointer self-center rounded-md focus-visible:ring-2 focus-visible:outline-none md:hidden"
                    :aria-label="t('common.menu')"
                    @click="isOpen = !isOpen">
                    <Icon name="menu" class="block h-5 w-5" />
                </button>
                <h1 class="truncate text-[19px] leading-tight font-bold tracking-[-0.01em]">
                    {{ pageTitle }}
                </h1>
                <span class="text-secondary-content hidden truncate text-[12.5px] md:inline">
                    {{ pageSub }}
                </span>
                <div class="ml-auto flex flex-none items-center gap-2 self-center">
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary hidden cursor-pointer items-center gap-1.5 rounded border px-2.5 py-1 text-[12.5px] whitespace-nowrap transition-colors sm:inline-flex"
                        :title="t('topbar.searchHint')"
                        @click="globalSearch">
                        <Icon name="search" class="h-3.5 w-3.5" aria-hidden="true" />
                        {{ t('topbar.search') }}
                    </button>
                    <button
                        v-if="uiStore.cta"
                        type="button"
                        class="bg-accent text-accent-content hover:brightness-106 cursor-pointer rounded border border-accent px-2.5 py-1 text-[12.5px] font-semibold whitespace-nowrap transition-colors"
                        @click="uiStore.cta.handler?.()">
                        {{ uiStore.cta.label }}
                    </button>
                    <LanguageDropdown />
                    <DropdownComponent ref="themeDropdownEl" width="medium" :label="t('common.theme')">
                        <template #button>
                            <Icon name="theme" class="h-4 w-4" />
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
                    <!-- enter-only fade: mode="out-in" dead-locks page swaps
                         in the embedded webview (leave hooks never settle and
                         the next component never mounts) -->
                    <transition name="fade">
                        <component :is="Component" :key="String($route.name)" />
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
import { useUiStore } from '@/store/ui'

import AsideNavigation from '@/components/layout/AsideNavigation.vue'
import DropdownComponent from '@/components/common/DropdownComponent.vue'
import LanguageDropdown from '@/components/common/LanguageDropdown.vue'
import ToastNotification from '@/components/common/ToastNotification.vue'
import Icon from '@/components/icons/Icon.vue'

const settingStore = useSettingStore()
const instanceStore = useInstanceStore()
const translationRequestStore = useTranslationRequestStore()
const uiStore = useUiStore()
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

/// Prototype topbar: page title + one-line context per screen. Pages may
/// override the sub (live counts) through the ui store.
const pageMeta: Record<string, { title: string; sub: string }> = {
    dashboard: { title: t('nav.dashboard'), sub: '' },
    library: { title: t('nav.library'), sub: t('topbar.sub.library') },
    queue: { title: t('nav.queue'), sub: '' },
    'queue-detail': { title: t('nav.queue'), sub: '' },
    import: { title: t('nav.import'), sub: '' },
    proofread: { title: t('nav.proofread'), sub: t('topbar.sub.proofread') },
    settings: { title: t('nav.settings'), sub: '' },
    'media-detail-show': { title: t('nav.library'), sub: '' },
    'media-detail-movie': { title: t('nav.library'), sub: '' }
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
const pageSub = computed(() => {
    if (uiStore.subOverride) return uiStore.subOverride
    if (String(route.name ?? '').startsWith('settings')) {
        const version = instanceStore.getVersion?.currentVersion
        return version ? `v${version}` : ''
    }
    return currentMeta.value.sub
})

/// ⌘K / the search button: land on the library and focus its search box.
const globalSearch = (): void => {
    if (route.name !== 'library') {
        void router.push({ name: 'library' })
    }
    uiStore.focusSearch()
}

const onGlobalKeydown = (event: KeyboardEvent): void => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        globalSearch()
    }
}

const setTheme = (theme: ITheme) => {
    instanceStore.storeTheme(theme)
    themeDropdownEl.value?.close()
}

const isOpen = computed({
    get: () => instanceStore.getIsOpen,
    set: (value: boolean) => instanceStore.setIsOpen(value)
})

onMounted(async () => {
    window.addEventListener('keydown', onGlobalKeydown)
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
    window.removeEventListener('keydown', onGlobalKeydown)
    uiStore.clearCta()
    uiStore.setSubOverride('')
    settingHubConnection.value?.off('SettingUpdate', onSettingUpdate)
    requestHubConnection.value?.off('ActiveTranslations', onActiveTranslations)
})
</script>
