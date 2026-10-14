<template>
    <!-- ═══ 修改密码表单(P0-3):强制改密页与认证管理卡共用。提交成功后
         服务端吊销该用户全部会话(含当前),故统一跳回登录页用新密码重登。
         错误展示按服务端 reason 码映射 i18n 文案。 -->
    <form class="space-y-6" data-test="change-password-form" @submit.prevent="handleSubmit">
        <div
            v-if="error"
            class="rounded-md border border-danger/40 bg-danger/15 p-4"
            data-test="change-password-error">
            <p class="text-sm text-danger">{{ error }}</p>
        </div>

        <div>
            <label for="currentPassword" class="mb-1 block text-sm">
                {{ t('changePassword.currentPassword') }}
            </label>
            <input
                id="currentPassword"
                v-model="currentPassword"
                type="password"
                autocomplete="current-password"
                :placeholder="t('changePassword.currentPassword')"
                data-test="change-password-current"
                class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                required />
        </div>

        <div>
            <label for="newPassword" class="mb-1 block text-sm">
                {{ t('changePassword.newPassword') }}
            </label>
            <input
                id="newPassword"
                v-model="newPassword"
                type="password"
                autocomplete="new-password"
                :placeholder="t('changePassword.newPassword')"
                data-test="change-password-new"
                class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                required />
            <p class="text-secondary-content mt-1.5 text-xs">
                {{ t('changePassword.hint') }}
            </p>
        </div>

        <div>
            <label for="confirmPassword" class="mb-1 block text-sm">
                {{ t('changePassword.confirmPassword') }}
            </label>
            <input
                id="confirmPassword"
                v-model="confirmPassword"
                type="password"
                autocomplete="new-password"
                :placeholder="t('changePassword.confirmPassword')"
                data-test="change-password-confirm"
                class="border-line w-full rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                required />
        </div>

        <div class="flex w-full items-center justify-end">
            <ButtonComponent type="submit" :disabled="loading" variant="primary" data-test="change-password-submit">
                <Icon
                    v-if="loading"
                    name="loaderCircle"
                    class="mr-2 h-4 w-4 animate-spin" />
                {{ loading ? t('changePassword.submitting') : t('changePassword.submit') }}
            </ButtonComponent>
        </div>
    </form>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import { authService } from '@/cells/app/api/authService'
import { ApiError } from '@/cells/app/api/apiError'
import { useInstanceStore } from '@/cells/app/stores/instance'
import { useToastStore } from '@/cells/app/stores/toast'
import Icon from '@/cells/kernel/components/icons/Icon.vue'
import ButtonComponent from '@/cells/kernel/components/ButtonComponent.vue'

const { t } = useI18n()
const router = useRouter()
const instanceStore = useInstanceStore()
const toastStore = useToastStore()
const authApi = authService(axios)

const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const error = ref('')

/// Same minimal policy the server enforces: length >= 8, one letter, one digit.
const isStrong = (password: string): boolean =>
    password.length >= 8 && /[a-zA-Z]/.test(password) && /\d/.test(password)

const reasonMessage = (reason: unknown, fallback: string): string => {
    switch (reason) {
        case 'invalid_current_password':
            return t('changePassword.errors.invalidCurrent')
        case 'weak_password':
        case 'validation':
            return t('changePassword.errors.weak')
        default:
            return fallback
    }
}

const handleSubmit = async (): Promise<void> => {
    error.value = ''

    if (!currentPassword.value || !newPassword.value || !confirmPassword.value) {
        error.value = t('changePassword.errors.required')
        return
    }
    if (newPassword.value !== confirmPassword.value) {
        error.value = t('changePassword.errors.mismatch')
        return
    }
    if (!isStrong(newPassword.value)) {
        error.value = t('changePassword.errors.weak')
        return
    }

    loading.value = true
    try {
        await authApi.changePassword({
            currentPassword: currentPassword.value,
            newPassword: newPassword.value
        })

        // Server semantics (P0-3): ALL sessions of the user were revoked —
        // including this one — so drop the local auth state and sign in
        // again with the new password.
        instanceStore.setAuthenticated(false)
        instanceStore.setMustChangePassword(false)
        toastStore.push({ message: t('changePassword.success'), type: 'success' })
        router.replace({ name: 'login' })
    } catch (err: unknown) {
        console.error('Password change failed:', err)
        const apiError = err instanceof ApiError ? err : undefined
        error.value = reasonMessage(
            (apiError?.data as { reason?: string } | undefined)?.reason,
            apiError?.data && typeof apiError.data === 'object' && 'message' in apiError.data
                ? String((apiError.data as { message?: string }).message)
                : t('changePassword.errors.generic')
        )
    } finally {
        loading.value = false
    }
}
</script>
