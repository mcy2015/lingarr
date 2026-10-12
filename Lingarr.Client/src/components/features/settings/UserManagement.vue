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
                        @click="authStore.createOrEditUser()">
                        {{ t('settings.users.create') }}
                    </ButtonComponent>
                </div>

                <div v-if="authStore.loading" class="flex justify-center py-8">
                    <LoaderCircleIcon class="h-8 w-8 animate-spin" />
                </div>

                <div>
                    <div class="border-accent grid grid-cols-12 border-b font-bold">
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
                            class="hover:bg-accent/5 bg-accent/10 border-accent grid grid-cols-12 border-b">
                            <div class="col-span-7 flex items-center px-4 py-2 md:col-span-4">
                                <div class="w-full">
                                    <InputComponent
                                        id="newUsername"
                                        v-model="authStore.editUsername"
                                        :placeholder="t('settings.users.username')"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :min-length="2"
                                        :error-message="t('settings.users.usernameError')"
                                        @update:validation="authStore.setUsernameValidation" />
                                </div>
                            </div>
                            <div
                                class="col-span-3 hidden items-center px-4 py-2 text-sm text-gray-400 md:flex">
                                -
                            </div>
                            <div
                                class="col-span-2 flex items-center justify-end gap-2 px-4 py-2 md:col-span-5">
                                <ButtonComponent
                                    variant="accent"
                                    size="xs"
                                    :disabled="authStore.loading"
                                    @click="authStore.cancelEdit">
                                    <span class="md:hidden">X</span>
                                    <span class="hidden md:block">
                                        {{ t('settings.users.cancel') }}
                                    </span>
                                </ButtonComponent>
                                <ButtonComponent
                                    variant="accent"
                                    size="xs"
                                    :disabled="!authStore.canSave || authStore.loading"
                                    :loading="authStore.loading"
                                    @click="authStore.saveUser()">
                                    {{ t('settings.users.createAction') }}
                                </ButtonComponent>
                            </div>
                        </div>
                        <div class="bg-accent/5 border-accent grid grid-cols-1 border-b">
                            <div class="col-span-1 px-4 py-2">
                                <div class="space-y-2">
                                    <InputComponent
                                        id="newPassword"
                                        v-model="authStore.editPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.passwordRequired')"
                                        :placeholder="t('settings.users.passwordPlaceholder')"
                                        :min-length="4"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="authStore.setPasswordValidation" />

                                    <InputComponent
                                        id="newConfirmPassword"
                                        v-model="authStore.editConfirmPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.confirmPasswordRequired')"
                                        :placeholder="
                                            t('settings.users.confirmPasswordPlaceholder')
                                        "
                                        :min-length="4"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="
                                            authStore.setConfirmPasswordValidation
                                        " />
                                </div>
                            </div>
                        </div>
                    </div>
                    <!-- Existing Users -->
                    <div v-for="user in authStore.users" :key="user.id">
                        <div class="hover:bg-accent/5 border-accent grid grid-cols-12 border-b">
                            <div class="col-span-7 flex items-center px-4 py-2 md:col-span-4">
                                <div v-if="authStore.editingUserId === user.id" class="w-full">
                                    <InputComponent
                                        id="editUsername"
                                        v-model="authStore.editUsername"
                                        :placeholder="t('settings.users.username')"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :min-length="2"
                                        :error-message="t('settings.users.usernameError')"
                                        @update:validation="authStore.setUsernameValidation" />
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
                                        @click="authStore.cancelEdit">
                                        <span class="md:hidden">X</span>
                                        <span class="hidden md:block">
                                            {{ t('settings.users.cancel') }}
                                        </span>
                                    </ButtonComponent>
                                    <ButtonComponent
                                        variant="accent"
                                        size="xs"
                                        :disabled="!authStore.canSave || authStore.loading"
                                        :loading="authStore.loading"
                                        @click="authStore.saveUser()">
                                        {{ t('settings.users.save') }}
                                    </ButtonComponent>
                                </template>
                                <template v-else>
                                    <ButtonComponent
                                        variant="accent"
                                        size="xs"
                                        @click="authStore.createOrEditUser(user)">
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
                            class="bg-accent/5 border-accent grid grid-cols-1 border-b">
                            <div class="col-span-1 px-4 py-2">
                                <div class="space-y-2">
                                    <InputComponent
                                        id="editPassword"
                                        v-model="authStore.editPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.newPasswordOptional')"
                                        :placeholder="t('settings.users.newPasswordPlaceholder')"
                                        :min-length="4"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="authStore.setPasswordValidation" />

                                    <InputComponent
                                        id="editConfirmPassword"
                                        v-model="authStore.editConfirmPassword"
                                        :validation-type="INPUT_VALIDATION_TYPE.STRING"
                                        :type="INPUT_TYPE.PASSWORD"
                                        :label="t('settings.users.confirmNewPasswordOptional')"
                                        :placeholder="
                                            t('settings.users.confirmNewPasswordPlaceholder')
                                        "
                                        :min-length="4"
                                        :error-message="t('settings.users.passwordError')"
                                        @update:validation="
                                            authStore.setConfirmPasswordValidation
                                        " />
                                </div>
                            </div>
                        </div>
                    </div>

                    <div
                        if="authStore.users.length === 0 && !authStore.createUser"
                        class="py-8 text-center text-gray-400">
                        {{ t('settings.users.noUsers') }}
                    </div>
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, IUser } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import LoaderCircleIcon from '@/components/icons/LoaderCircleIcon.vue'
import StatusMessage from '@/components/common/StatusMessage.vue'
import { useAuthStore } from '@/store/auth'
import { formatDateTime } from '@/utils/date'

const authStore = useAuthStore()
const { t } = useI18n()

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
