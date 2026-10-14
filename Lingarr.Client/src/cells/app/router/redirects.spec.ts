// @vitest-environment happy-dom
// Legacy bookmark redirects (IA 重构批 2 回归):旧设置路径全部落到新家,
// /settings/automation 从"指向媒体与自动化"的旧重定向升级为自动化 tab 本体。
import { describe, expect, it } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { routes } from '@/cells/app/router'

describe('settings legacy redirects', () => {
    it.each([
        ['/settings', 'settings-providers'],
        ['/settings/services', 'settings-providers'],
        ['/settings/integration', 'settings-sources'],
        ['/settings/subtitle', 'settings-quality'],
        ['/settings/tasks', 'schedule'],
        ['/settings/logs', 'logs']
    ])('%s resolves to %s', async (path, expected) => {
        const router = createRouter({ history: createMemoryHistory(), routes })
        await router.push(path)
        expect(router.currentRoute.value.name).toBe(expected)
    })

    it('/settings/automation is the automation tab itself now', async () => {
        const router = createRouter({ history: createMemoryHistory(), routes })
        await router.push('/settings/automation')
        expect(router.currentRoute.value.name).toBe('settings-automation')
    })
})
