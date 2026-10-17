// @vitest-environment happy-dom
// setting store · 加密设置保存失败路径验收:API 拒绝时本地「已配置」镜像
// 回滚到原值(消除"已配置"假象,placeholder 不再谎报)、toast 报
// common.saveFailed、返回 false;成功路径返回 true 且镜像翻转;
// 校验失败不发请求、不弹 toast。
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import i18n from '@/i18n'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'

const setEncryptedSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setEncryptedSetting: setEncryptedSettingMock,
        setSetting: vi.fn(),
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: vi.fn().mockResolvedValue({})
    })
}))

beforeEach(() => {
    setActivePinia(createPinia())
    setEncryptedSettingMock.mockReset()
})

describe('useSettingStore · updateEncryptedSetting', () => {
    it('returns true and flips the configured flag when the API accepts the write', async () => {
        setEncryptedSettingMock.mockResolvedValue(undefined)
        const store = useSettingStore()

        const result = await store.updateEncryptedSetting('openai_api_key', 'sk-new-token')

        expect(result).toBe(true)
        expect(setEncryptedSettingMock).toHaveBeenCalledWith('openai_api_key', 'sk-new-token')
        expect(store.isEncryptedSettingConfigured('openai_api_key')).toBe(true)
        expect(useToastStore().toasts).toHaveLength(0)
    })

    it('returns false, keeps the previous configured state and toasts when the API rejects the write', async () => {
        setEncryptedSettingMock.mockRejectedValue(
            new Error('Request failed with status code 400')
        )
        const store = useSettingStore()
        // 之前已配置:旧密钥仍在服务端,失败后不得被打成「未配置」
        store.setEncryptedSettingState('openai_api_key', true)

        const result = await store.updateEncryptedSetting('openai_api_key', 'sk-broken-token')

        expect(result).toBe(false)
        expect(store.isEncryptedSettingConfigured('openai_api_key')).toBe(true)

        const toasts = useToastStore().toasts
        expect(toasts).toHaveLength(1)
        expect(toasts[0].type).toBe('error')
        // 键必须真实解析(键缺失时 vue-i18n 回退为键名本身)
        expect(toasts[0].message).not.toBe('common.saveFailed')
        expect(toasts[0].message).toBe(i18n.global.t('common.saveFailed'))
    })

    it('rolls an unconfigured key back to unconfigured when the write fails', async () => {
        setEncryptedSettingMock.mockRejectedValue(new Error('400'))
        const store = useSettingStore()

        const result = await store.updateEncryptedSetting('openai_api_key', 'sk-doomed')

        expect(result).toBe(false)
        // 之前未配置:失败后仍为未配置 —— 不产生「已配置」假象
        expect(store.isEncryptedSettingConfigured('openai_api_key')).toBe(false)
        expect(useToastStore().toasts).toHaveLength(1)
    })

    it('does not call the API and reports failure when the value is invalid', async () => {
        const store = useSettingStore()

        const result = await store.updateEncryptedSetting(
            'openai_api_key',
            'typed-garbage',
            false
        )

        expect(result).toBe(false)
        expect(setEncryptedSettingMock).not.toHaveBeenCalled()
        // 校验失败仍如实镜像「输入非空」,但无保存发生也不弹 toast
        expect(store.isEncryptedSettingConfigured('openai_api_key')).toBe(true)
        expect(useToastStore().toasts).toHaveLength(0)
    })
})
