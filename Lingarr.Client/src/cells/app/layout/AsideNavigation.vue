<template>
    <div class="relative">
        <!-- Backdrop -->
        <div
            v-if="isOpen"
            class="bg-opacity-50 fixed inset-0 z-40 bg-black md:hidden"
            @click="isOpen = false"></div>
        <!-- Aside -->
        <aside
            :class="
                isOpen
                    ? 'translate-x-0'
                    : 'invisible -translate-x-full md:visible md:translate-x-0'
            "
            class="border-line bg-secondary fixed top-0 left-0 z-50 flex h-dvh w-64 flex-col overflow-hidden border-r transition-[transform,visibility] duration-300 ease-in-out md:sticky">
            <button
                type="button"
                class="focus-visible:ring-accent absolute top-1 right-1 cursor-pointer rounded-md p-1 focus-visible:ring-2 focus-visible:outline-none md:hidden"
                :aria-label="t('common.close')"
                @click="isOpen = false">
                <Icon name="times" class="block h-6 w-6" />
            </button>
            <div class="px-5 pt-[18px] pb-4">
                <h1
                    class="text-[17px] font-bold tracking-[0.04em] text-primary-content">
                    LINGARR<span class="text-accent animate-blink">_</span>
                </h1>
            </div>
            <!-- Navigation -->
            <nav class="grow overflow-y-auto px-3 py-4">
                <ul class="space-y-1">
                    <li v-for="(item, index) in menuItems" :key="index">
                        <router-link
                            :to="{ name: item.route }"
                            class="flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors"
                            :class="
                                isActive(item)
                                    ? 'bg-tertiary text-primary-content shadow-[inset_2px_0_0_0_var(--accent)]'
                                    : 'text-secondary-content/70 hover:bg-tertiary/50 hover:text-primary-content'
                            "
                            @click="isOpen = false">
                            <Icon :name="item.icon" class="h-4 w-4 flex-none" />
                            <div class="relative">
                                {{ t(item.label) }}

                                    <span
                                    v-if="item.route == 'queue' && activeRequests > 0"
                                    class="bg-accent text-accent-content absolute -top-1 -right-5 inline-flex items-center justify-center rounded-full px-1.5 py-0.5 font-mono text-xs leading-none font-bold">
                                    {{ activeRequests }}
                                </span>
                            </div>
                        </router-link>
                    </li>
                </ul>
            </nav>
            <!-- Version and media section -->
            <div class="pointer-events-none h-64 w-full">
                <img
                    v-if="instanceStore.getPoster"
                    :src="`/api/image/${instanceStore.getPoster}`"
                    class="mask-gradient h-full w-full object-cover"
                    alt=""
                    loading="lazy"
                    decoding="async" />
                <div
                    v-if="instanceStore.getVersion.currentVersion.length"
                    class="absolute right-0 bottom-0 flex w-full justify-center p-4">
                    <BadgeComponent
                        v-if="instanceStore.getVersion.isDevelopment"
                        classes="text-warning-content border-warning/40 bg-warning/50">
                        {{ t('version.development') }}
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
import { useInstanceStore } from '@/cells/app/stores/instance'
import { useTranslationRequestStore } from '@/cells/app/stores/translationRequest'
import { MenuItem } from '@/types'
import BadgeComponent from '@/cells/kernel/components/BadgeComponent.vue'
import Icon from '@/cells/kernel/components/icons/Icon.vue'

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
    { label: 'nav.dashboard', icon: 'home', route: 'dashboard', children: [] },
    { label: 'nav.library', icon: 'movie', route: 'library', children: [] },
    { label: 'nav.queue', icon: 'language', route: 'queue', children: [] },
    { label: 'nav.import', icon: 'folders', route: 'import', children: [] },
    { label: 'nav.proofread', icon: 'checkMarkCircle', route: 'proofread', children: [] },
    {
        label: 'nav.settings',
        icon: 'setting',
        route: 'settings',
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
