<template>
    <!-- ═══ 强制改密页(P0-3):must_change_password=true 时路由守卫把所有
         认证页面重定向到这里;页面不可跳过,改完密码(服务端吊销全部会话)
         自动回到登录页。 -->
    <div class="flex min-h-screen items-center justify-center px-4">
        <div class="w-full max-w-sm">
            <CardComponent :title="t('changePassword.forcedTitle')">
                <template #content>
                    <p class="text-secondary-content mb-6 text-sm leading-relaxed">
                        {{ t('changePassword.forcedIntro') }}
                    </p>
                    <ChangePasswordForm />
                </template>
            </CardComponent>
        </div>
    </div>
</template>
<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'
import ChangePasswordForm from '@/cells/app/components/ChangePasswordForm.vue'

const { t } = useI18n()
</script>
