<template>
    <!-- ═══ 更改用户名卡:单用户账户改名,验证当前密码后生效。与改密不同,
         服务端不吊销会话(票据携带的是不变的 user id),改名后无需重登。
         仅在认证开启时显示(关闭时无 Cookie 会话,端点本就 401)。 -->
    <div
        v-if="authEnabled == 'true'"
        class="border-line bg-secondary rounded-md border"
        data-test="change-username-card">
        <div class="border-line flex flex-wrap items-center gap-2.5 border-b px-4 py-3">
            <h2 class="text-[13px] font-semibold">{{ t('changeUsername.title') }}</h2>
            <span class="text-secondary-content ml-auto text-[11px]">
                {{ t('changeUsername.cardNote') }}
            </span>
        </div>

        <form class="space-y-4 px-4 py-3" data-test="change-username-form" @submit.prevent="handleSubmit">
            <div
                v-if="error"
                class="rounded-md border border-danger/40 bg-danger/15 p-3"
                data-test="change-username-error">
                <p class="text-sm text-danger">{{ error }}</p>
            </div>

            <div>
                <label for="newUsername" class="mb-1 block text-sm">
                    {{ t('changeUsername.newUsername') }}
                </label>
                <input
                    id="newUsername"
                    v-model="newUsername"
                    type="text"
                    autocomplete="username"
                    :placeholder="t('changeUsername.newUsername')"
                    data-test="change-username-input"
                    class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                    required />
                <p class="text-secondary-content mt-1.5 text-xs">
                    {{ t('changeUsername.hint') }}
                </p>
            </div>

            <div>
                <label for="usernamePassword" class="mb-1 block text-sm">
                    {{ t('changePassword.currentPassword') }}
                </label>
                <input
                    id="usernamePassword"
                    v-model="currentPassword"
                    type="password"
                    autocomplete="current-password"
                    :placeholder="t('changePassword.currentPassword')"
                    data-test="change-username-password"
                    class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                    required />
            </div>

            <div class="flex w-full items-center justify-end">
                <ButtonComponent
                    type="submit"
                    :disabled="loading"
                    variant="primary"
                    data-test="change-username-submit">
                    <Icon
                        v-if="loading"
                        name="loaderCircle"
                        class="mr-2 h-4 w-4 animate-spin" />
                    {{ loading ? t('changePassword.submitting') : t('changeUsername.submit') }}
                </ButtonComponent>
            </div>
        </form>
    </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import axios from 'axios'
import { SETTINGS } from '@/types'
import { authService } from '@/cells/app/api/authService'
import { ApiError } from '@/cells/app/api/apiError'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'

const { t } = useI18n()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const authApi = authService(axios)

const authEnabled = computed(
    () => (settingStore.getSetting(SETTINGS.AUTH_ENABLED) as string) ?? 'false'
)

const newUsername = ref('')
const currentPassword = ref('')
const loading = ref(false)
const error = ref('')

/// Same policy the server enforces: 3-32 chars of letters, digits, dot,
/// dash or underscore.
const isValidUsername = (username: string): boolean => /^[A-Za-z0-9._-]{3,32}$/.test(username)

const reasonMessage = (reason: unknown, fallback: string): string => {
    switch (reason) {
        case 'invalid_username':
        case 'validation':
            return t('changeUsername.errors.invalid')
        case 'same_username':
            return t('changeUsername.errors.same')
        case 'username_taken':
            return t('changeUsername.errors.taken')
        case 'invalid_current_password':
            return t('changePassword.errors.invalidCurrent')
        default:
            return fallback
    }
}

const handleSubmit = async (): Promise<void> => {
    error.value = ''

    if (!newUsername.value || !currentPassword.value) {
        error.value = t('changePassword.errors.required')
        return
    }
    if (!isValidUsername(newUsername.value)) {
        error.value = t('changeUsername.errors.invalid')
        return
    }

    loading.value = true
    try {
        const response = await authApi.changeUsername({
            currentPassword: currentPassword.value,
            newUsername: newUsername.value.trim()
        })
        toastStore.push({
            message: t('changeUsername.success', { username: response.username }),
            type: 'success'
        })
        newUsername.value = ''
        currentPassword.value = ''
    } catch (err: unknown) {
        console.error('Username change failed:', err)
        const apiError = err instanceof ApiError ? err : undefined
        error.value = reasonMessage(
            (apiError?.data as { reason?: string } | undefined)?.reason,
            apiError?.data && typeof apiError.data === 'object' && 'message' in apiError.data
                ? String((apiError.data as { message?: string }).message)
                : t('changeUsername.errors.generic')
        )
    } finally {
        loading.value = false
    }
}
</script>
