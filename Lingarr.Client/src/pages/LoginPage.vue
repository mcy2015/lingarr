<template>
    <div class="flex min-h-screen items-center justify-center px-4">
        <div class="w-full max-w-sm">
            <CardComponent :title="t('login.title')">
                <template #content>
                    <div
                        v-if="error"
                        class="mb-6 rounded-md border border-danger/40 bg-danger/15 p-4">
                        <p class="text-sm text-danger">{{ error }}</p>
                    </div>

                    <form class="space-y-6" @submit.prevent="handleLogin">
                        <div>
                            <label for="username" class="mb-1 block text-sm">
                                {{ t('login.username') }}
                            </label>
                            <input
                                id="username"
                                v-model="username"
                                type="text"
                                autocomplete="username"
                                :placeholder="t('login.usernamePlaceholder')"
                                class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                                required />
                        </div>

                        <div>
                            <label for="password" class="mb-1 block text-sm">
                                {{ t('login.password') }}
                            </label>
                            <input
                                id="password"
                                v-model="password"
                                type="password"
                                autocomplete="current-password"
                                :placeholder="t('login.passwordPlaceholder')"
                                class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                                required />
                        </div>

                        <div class="flex w-full items-center justify-end">
                            <ButtonComponent type="submit" :disabled="loading" variant="primary">
                                <Icon
                                    v-if="loading"
                                    name="loaderCircle"
                                    class="mr-2 h-4 w-4 animate-spin" />
                                {{ loading ? t('login.signingIn') : t('login.signIn') }}
                            </ButtonComponent>
                        </div>
                    </form>
                </template>
            </CardComponent>
        </div>
    </div>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import services from '@/services'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'

const router = useRouter()
const { t } = useI18n()

const loading = ref(false)
const error = ref('')

const username = ref('')
const password = ref('')

const handleLogin = async () => {
    if (!username.value || !password.value) {
        error.value = t('login.errors.missingCredentials')
        return
    }

    loading.value = true
    error.value = ''

    try {
        await services.auth.login({
            username: username.value,
            password: password.value
        })

        router.replace({ name: 'dashboard' })
    } catch (err: any) {
        console.error('Login error:', err)
        error.value = err?.data?.message || t('login.errors.invalidCredentials')
    } finally {
        loading.value = false
    }
}
</script>
