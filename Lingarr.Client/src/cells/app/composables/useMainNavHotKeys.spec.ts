// @vitest-environment happy-dom
// Main-nav hotkeys 1-6 (prototype global-interactions keyboard table):
// each digit switches to its screen; typing targets are exempt; modifiers
// never trigger a navigation.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { mount, VueWrapper } from '@vue/test-utils'
import { useMainNavHotKeys } from '@/cells/app/composables/useMainNavHotKeys'

const Host = defineComponent({
    props: {
        router: { type: Object as () => Router, required: true }
    },
    setup(props) {
        useMainNavHotKeys(props.router)
    },
    template: '<div />'
})

const makeRouter = (): Router =>
    createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'dashboard', component: { template: '<div />' } },
            { path: '/library', name: 'library', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } },
            { path: '/import', name: 'import', component: { template: '<div />' } },
            { path: '/proofread', name: 'proofread', component: { template: '<div />' } },
            { path: '/settings', name: 'settings', component: { template: '<div />' } }
        ]
    })

const press = (key: string, target?: Element, modifiers: KeyboardEventInit = {}): KeyboardEvent => {
    const event = new KeyboardEvent('keydown', {
        key,
        cancelable: true,
        bubbles: true,
        ...modifiers
    })
    if (target) {
        target.dispatchEvent(event)
    } else {
        window.dispatchEvent(event)
    }
    return event
}

let host: VueWrapper | null = null

const mountHost = async (): Promise<Router> => {
    const router = makeRouter()
    host = mount(Host, { props: { router } })
    await router.push('/')
    await router.isReady()
    return router
}

afterEach(() => {
    host?.unmount()
    host = null
    vi.restoreAllMocks()
})

describe('useMainNavHotKeys (keys 1-6)', () => {
    it.each([
        ['1', 'dashboard'],
        ['2', 'library'],
        ['3', 'queue'],
        ['4', 'import'],
        ['5', 'proofread'],
        ['6', 'settings']
    ])('key %s navigates to %s', async (key, routeName) => {
        const router = await mountHost()

        press(key)

        // router.push runs async inside the composable — wait for the route
        await vi.waitFor(() => expect(router.currentRoute.value.name).toBe(routeName))
    })

    it('swallows the default browser behaviour on a handled key', async () => {
        await mountHost()

        const event = press('3')

        expect(event.defaultPrevented).toBe(true)
    })

    it('does not navigate while typing in an input', async () => {
        const router = await mountHost()

        const input = document.createElement('input')
        document.body.appendChild(input)
        input.focus()

        const event = press('3', input)
        expect(event.defaultPrevented).toBe(false)
        expect(router.currentRoute.value.name).toBe('dashboard')

        input.remove()
    })

    it('does not navigate for other keys or modifier combos', async () => {
        const router = await mountHost()

        press('0')
        expect(router.currentRoute.value.name).toBe('dashboard')

        press('7')
        expect(router.currentRoute.value.name).toBe('dashboard')

        const meta = press('2', undefined, { metaKey: true })
        expect(meta.defaultPrevented).toBe(false)
        expect(router.currentRoute.value.name).toBe('dashboard')
    })

    it('stops listening after the host unmounts', async () => {
        const router = await mountHost()
        host?.unmount()
        host = null

        press('2')

        expect(router.currentRoute.value.name).toBe('dashboard')
    })
})
