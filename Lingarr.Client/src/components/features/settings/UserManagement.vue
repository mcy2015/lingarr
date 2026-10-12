<template>
    <CardComponent :title="t('settings.users.title')">
        <template #description></template>
        <template #content>
            <div class="flex flex-col space-y-2 pb-4">
                <StatusMessage :message="authStore.error" type="error" />
                <StatusMessage :message="authStore.success" type="success" />

                <div class="flex justify-end">
                    <ButtonComponent
                        variant="accent"
                        size="xs"
                        :disabled="authStore.isCreating || authStore.editingUserId !== null"
                        @click="openCreate">
                        {{ t('settings.users.create') }}
                    </ButtonComponent>
                </div>

                <div v-if="authStore.loading" class="flex justify-center py-8">
                    <Icon name="loaderCircle" class="h-8 w-8 animate-spin" />
                </div>

                <div>
                    <div class="border-line grid grid-cols-12 border-b font-bold">
                        <div class="col-span-7 px-4 py-2 md:col-span-4">
                            {{ t('settings.users.username') }}
                        </div>
                        <div class="col-span-3 hidden px-4 py-2 md:block">
                            {{ t('settings.users.lastLogin') }}
                        </div>
                        <div class="col-span-2 flex justify-end px-4 py-2 md:col-span-5">
                            {{ t('settings.users.actions') }}
                        </div>
                    </div>
                    <div v-if="authStore.isCreatingUser">
                        <div
                            class="hover:bg-accent/5 bg-accent/10 border-line grid grid-cols-12 border-b">
                            <div class="col-span-7 flex items-center px-4 py-2 md:col-span-4">
                                <div class="w-full">
                                    <InputComponent
                                        id="newUsername"
                                        v-model="editUsername"
                                        :placeholder="t('settings.users.username')"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :min-length="2"
                                        :error-message="t('settings.users.usernameError')"
                                        @update:validation="setUsernameValidity" />
                                </div>
                            </div>
                            <div
                                class="text-secondary-content/60 col-span-3 hidden items-center px-4 py-2 text-sm md:flex">
                                -
                            </div>
                            <div
                                class="col-span-2 flex items-center justify-end gap-2 px-4 py-2 md:col-span-5">
                                <ButtonComponent
                                    variant="accent"
                                    size="xs"
                                    :disabled="authStore.loading"
                                    @click="cancelEdit">
                                    <span class="md:hidden">X</span>
                                    <span class="hidden md:block">
                                        {{ t('settings.users.cancel') }}
                                    </span>
                                </ButtonComponent>
                                <ButtonComponent
                                    variant="accent"
                                    size="xs"
                                    :disabled="!canSave || authStore.loading"
                                    :loading="authStore.loading"
                                    @click="saveUser">
                                    {{ t('settings.users.createAction') }}
                                </ButtonComponent>
                            </div>
                        </div>
                        <div class="bg-accent/5 border-line grid grid-cols-1 border-b">
                            <div class="col-span-1 px-4 py-2">
                                <div class="space-y-2">
                                    <InputComponent
                                        id="newPassword"
                                        v-model="editPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.passwordRequired')"
                                        :placeholder="t('settings.users.passwordPlaceholder')"
                                        :min-length="8"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="setPasswordValidity" />

                                    <InputComponent
                                        id="newConfirmPassword"
                                        v-model="editConfirmPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.confirmPasswordRequired')"
                                        :placeholder="
                                            t('settings.users.confirmPasswordPlaceholder')
                                        "
                                        :min-length="8"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="setConfirmPasswordValidity" />
                                </div>
                            </div>
                        </div>
                    </div>
                    <!-- Existing Users -->
                    <div v-for="user in authStore.users" :key="user.id">
                        <div class="hover:bg-accent/5 border-line grid grid-cols-12 border-b">
                            <div class="col-span-7 flex items-center px-4 py-2 md:col-span-4">
                                <div v-if="authStore.editingUserId === user.id" class="w-full">
                                    <InputComponent
                                        id="editUsername"
                                        v-model="editUsername"
                                        :placeholder="t('settings.users.username')"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :min-length="2"
                                        :error-message="t('settings.users.usernameError')"
                                        @update:validation="setUsernameValidity" />
                                </div>
                                <span v-else class="text-sm">{{ user.username }}</span>
                            </div>
                            <div
                                class="col-span-3 hidden items-center px-4 py-2 text-sm whitespace-nowrap md:flex">
                                {{
                                    user.lastLoginAt
                                        ? formatDateTime(user.lastLoginAt)
                                        : t('settings.users.never')
                                }}
                            </div>
                            <div
                                class="col-span-2 flex items-center justify-end gap-2 px-4 py-2 md:col-span-5">
                                <template v-if="authStore.editingUserId === user.id">
                                    <ButtonComponent
                                        variant="accent"
                                        size="xs"
                                        :disabled="authStore.loading"
                                        @click="cancelEdit">
                                        <span class="md:hidden">X</span>
                                        <span class="hidden md:block">
                                            {{ t('settings.users.cancel') }}
                                        </span>
                                    </ButtonComponent>
                                    <ButtonComponent
                                        variant="accent"
                                        size="xs"
                                        :disabled="!canSave || authStore.loading"
                                        :loading="authStore.loading"
                                        @click="saveUser">
                                        {{ t('settings.users.save') }}
                                    </ButtonComponent>
                                </template>
                                <template v-else>
                                    <ButtonComponent
                                        variant="accent"
                                        size="xs"
                                        @click="openEdit(user)">
                                        {{ t('settings.users.edit') }}
                                    </ButtonComponent>
                                    <ButtonComponent
                                        v-if="authStore.users.length >= 2"
                                        variant="accent"
                                        size="xs"
                                        :disabled="authStore.deletingUserId === user.id"
                                        :loading="authStore.deletingUserId === user.id"
                                        @click="deleteUserConfirm(user)">
                                        {{ t('settings.users.delete') }}
                                    </ButtonComponent>
                                </template>
                            </div>
                        </div>
                        <div
                            v-if="authStore.editingUserId === user.id"
                            class="bg-accent/5 border-line grid grid-cols-1 border-b">
                            <div class="col-span-1 px-4 py-2">
                                <div class="space-y-2">
                                    <InputComponent
                                        id="editPassword"
                                        v-model="editPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.newPasswordOptional')"
                                        :placeholder="t('settings.users.newPasswordPlaceholder')"
                                        :min-length="8"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="setPasswordValidity" />

                                    <InputComponent
                                        id="editConfirmPassword"
                                        v-model="editConfirmPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.confirmNewPasswordOptional')"
                                        :placeholder="
                                            t('settings.users.confirmNewPasswordPlaceholder')
                                        "
                                        :min-length="8"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="setConfirmPasswordValidity" />
                                </div>
                            </div>
                        </div>
                    </div>

                    <div
                        if="authStore.users.length === 0 && !authStore.createUser"
                        class="text-secondary-content/60 py-8 text-center">
                        {{ t('settings.users.noUsers') }}
                    </div>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, IUser } from '@/types'
import CardComponent from '@/components/common/CardComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import Icon from '@/components/icons/Icon.vue'
import StatusMessage from '@/components/common/StatusMessage.vue'
import { useAuthStore } from '@/store/auth'
import { formatDateTime } from '@/utils/date'

const authStore = useAuthStore()
const { t } = useI18n()

// Editor form state lives here; the store only tracks users and which row is being edited.
const editUsername = ref('')
const editPassword = ref('')
const editConfirmPassword = ref('')
const isUsernameValid = ref(false)
const isPasswordValid = ref(true)
const isConfirmPasswordValid = ref(true)

const canSave = computed<boolean>(() => {
    if (!isUsernameValid.value) {
        return false
    }

    // new users, password is required
    if (authStore.isCreating) {
        if (!editPassword.value || !isPasswordValid.value || !isConfirmPasswordValid.value) {
            return false
        }
        if (editPassword.value !== editConfirmPassword.value) {
            return false
        }
    }

    // existing users, password is optional
    if (editPassword.value || editConfirmPassword.value) {
        if (!isPasswordValid.value || !isConfirmPasswordValid.value) {
            return false
        }
        if (editPassword.value !== editConfirmPassword.value) {
            return false
        }
    }
    return true
})

const resetForm = () => {
    editUsername.value = ''
    editPassword.value = ''
    editConfirmPassword.value = ''
    isUsernameValid.value = false
    isPasswordValid.value = true
    isConfirmPasswordValid.value = true
}

const setUsernameValidity = (valid: boolean) => {
    isUsernameValid.value = valid
}

const setPasswordValidity = (valid: boolean) => {
    isPasswordValid.value = valid
}

const setConfirmPasswordValidity = (valid: boolean) => {
    isConfirmPasswordValid.value = valid
}

const openCreate = () => {
    resetForm()
    authStore.startCreate()
}

const openEdit = (user: IUser) => {
    editUsername.value = user.username
    editPassword.value = ''
    editConfirmPassword.value = ''
    isUsernameValid.value = true
    isPasswordValid.value = true
    isConfirmPasswordValid.value = true
    authStore.startEdit(user)
}

const cancelEdit = () => {
    authStore.cancelEdit()
    resetForm()
}

const saveUser = async () => {
    if (!canSave.value || authStore.loading) {
        return
    }
    await authStore.saveUser({
        username: editUsername.value,
        password: editPassword.value
    })
}

// The store closes the editor after a successful save; clear the fields with it.
// On failure the editor stays open so entered values are kept.
watch(
    () => authStore.isCreating || authStore.editingUserId !== null,
    (active) => {
        if (!active) {
            resetForm()
        }
    }
)

const deleteUserConfirm = async (user: IUser) => {
    if (!confirm(t('settings.users.deleteConfirm', { name: user.username }))) {
        return
    }
    await authStore.deleteUser(user)
}

onMounted(async () => {
    await authStore.loadUsers()
})
</script>
