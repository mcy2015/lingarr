<template>
    <div
        :class="[`${instanceStore.getTheme}`, 'flex min-h-screen bg-primary text-primary-content']">
        <div class="flex w-full flex-col drop-shadow-xl">
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
import { useInstanceStore } from '@/cells/app/stores/instance'
const instanceStore = useInstanceStore()
</script>
