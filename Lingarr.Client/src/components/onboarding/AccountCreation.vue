<template>
    <div class="space-y-6">
        <div>
            <h2 class="mb-4 text-xl font-semibold text-primary-content">
                {{ t('onboarding.account.title') }}
            </h2>
            <p class="mb-6 text-sm text-secondary-content/60">
                {{ t('onboarding.account.description') }}
            </p>
        </div>

        <InputComponent
            id="username"
            v-model="username"
            :label="t('onboarding.account.username')"
            :placeholder="t('onboarding.account.usernamePlaceholder')"
            :validation-type="INPUT_VALIDATION_TYPE.STRING"
            :min-length="2"
            :error-message="t('onboarding.account.usernameError')"
            @update:validation="(valid: boolean) => updateValidation('username', valid)" />

        <InputComponent
            id="password"
            v-model="password"
            :label="t('onboarding.account.password')"
            :validation-type="INPUT_VALIDATION_TYPE.STRING"
            :type="INPUT_TYPE.PASSWORD"
            :placeholder="t('onboarding.account.passwordPlaceholder')"
            :min-length="4"
            :error-message="t('onboarding.account.passwordError')"
            @update:validation="(valid: boolean) => updateValidation('password', valid)" />

        <InputComponent
            id="confirmPassword"
            v-model="confirmPassword"
            :label="t('onboarding.account.confirmPassword')"
            :validation-type="INPUT_VALIDATION_TYPE.STRING"
            :type="INPUT_TYPE.PASSWORD"
            :placeholder="t('onboarding.account.confirmPasswordPlaceholder')"
            :min-length="4"
            :error-message="t('onboarding.account.passwordError')"
            @update:validation="(valid: boolean) => updateValidation('confirmPassword', valid)" />
    </div>
</template>

<script setup lang="ts">
import InputComponent from '@/components/common/InputComponent.vue'
import { useI18n } from 'vue-i18n'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE } from '@/ts'

const { t } = useI18n()

const username = defineModel<string>('username', { default: '' })
const password = defineModel<string>('password', { default: '' })
const confirmPassword = defineModel<string>('confirmPassword', { default: '' })

const emit = defineEmits<{
    'update:validation': [field: string, valid: boolean]
}>()

const updateValidation = (field: string, valid: boolean) => {
    emit('update:validation', field, valid)
}
</script>
