// @vitest-environment happy-dom
// P0-3 router guard: while the server-side must_change_password flag is set,
// every authenticated route funnels into the forced change screen; without a
// session everything falls back to the login page.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { authGuard } from '@/cells/app/router'
import { useInstanceStore } from '@/cells/app/stores/instance'
import type { RouteLocationNormalized } from 'vue-router'

const location = (name: string, authenticated: boolean): RouteLocationNormalized =>
    ({
        name,
        path: `/${name}`,
        fullPath: `/${name}`,
        params: {},
        query: {},
        hash: '',
        matched: [],
        meta: { authenticated },
        redirectedFrom: undefined,
        href: `/${name}`
    }) as unknown as RouteLocationNormalized

let instanceStore: ReturnType<typeof useInstanceStore>

beforeEach(() => {
    setActivePinia(createPinia())
    instanceStore = useInstanceStore()
})

describe('authGuard · 未认证', () => {
    it('sends authenticated routes to the login page', async () => {
        vi.spyOn(instanceStore, 'ensureAuthenticated').mockResolvedValue(false)

        const result = await authGuard(location('dashboard', true))

        expect(result).toEqual({ name: 'login' })
    })

    it('leaves unauthenticated routes alone', async () => {
        const ensure = vi.spyOn(instanceStore, 'ensureAuthenticated')

        const result = await authGuard(location('login', false))

        expect(result).toBe(true)
        expect(ensure).not.toHaveBeenCalled()
    })
})

describe('authGuard · 强制改密(P0-3)', () => {
    it('funnels authenticated routes into the change screen while the flag is set', async () => {
        instanceStore.authenticated = true
        instanceStore.mustChangePassword = true

        const result = await authGuard(location('dashboard', true))

        expect(result).toEqual({ name: 'change-password' })
    })

    it('lets the change screen itself through', async () => {
        instanceStore.authenticated = true
        instanceStore.mustChangePassword = true

        const result = await authGuard(location('change-password', true))

        expect(result).toBe(true)
    })

    it('returns normal navigation once the flag is cleared', async () => {
        instanceStore.authenticated = true
        instanceStore.mustChangePassword = false

        const result = await authGuard(location('dashboard', true))

        expect(result).toBe(true)
    })
})

// The guard must consult ensureAuthenticated() before deciding, so the
// mustChangePassword flag it reads is the one /api/auth/authenticated
// reports — not a parallel source of truth.
describe('authGuard · ensureAuthenticated 集成', () => {
    it('awaits the authenticated probe before allowing navigation', async () => {
        const ensure = vi.spyOn(instanceStore, 'ensureAuthenticated').mockResolvedValue(true)

        const result = await authGuard(location('dashboard', true))

        expect(ensure).toHaveBeenCalled()
        expect(result).toBe(true)
    })
})
