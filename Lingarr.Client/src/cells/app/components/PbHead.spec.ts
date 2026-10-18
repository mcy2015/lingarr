// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { type RouteLocationNormalized, type RouteLocationRaw } from 'vue-router'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import PbHead from '@/cells/app/components/PbHead.vue'
import { previousRoute } from '@/cells/app/router'

interface HeadProps {
    backTo?: RouteLocationRaw
    backLabel?: string
    title: string
    sub?: string
}

const mountHead = async (props: HeadProps = { title: 'Detail' }) => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    const i18n = createI18n({ legacy: false, locale: 'en', messages: { en: {} } })
    const wrapper = mount(PbHead, {
        props,
        slots: {
            actions: '<button data-test="slot-action" type="button">Save</button>'
        },
        global: { plugins: [router, i18n] }
    })
    return { wrapper, router }
}

describe('PbHead (drill-down page header)', () => {
    it('renders the title block with the mono sub line and the back label', async () => {
        const { wrapper } = await mountHead({
            backTo: { name: 'queue' },
            backLabel: 'Queue',
            title: 'Shōgun — S01E05',
            sub: '#2417 · EN → ZH · 512 lines'
        })

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain('Queue')
        expect(wrapper.text()).toContain('Shōgun — S01E05')
        const sub = wrapper.find('div.mono')
        expect(sub.exists()).toBe(true)
        expect(sub.text()).toBe('#2417 · EN → ZH · 512 lines')
    })

    it('hides the back button and sub line when their props are omitted', async () => {
        const { wrapper } = await mountHead({ title: 'Logs' })

        expect(wrapper.find('[data-test="pb-back"]').exists()).toBe(false)
        expect(wrapper.find('div.mono').exists()).toBe(false)
    })

    it('navigates to the back target when the back button is clicked', async () => {
        const { wrapper, router } = await mountHead({
            backTo: { name: 'queue' },
            backLabel: 'Queue',
            title: 'Detail'
        })

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('queue')
    })

    it('goes back in history when the previous entry is the back target, keeping its query', async () => {
        const router = createRouter({
            history: createMemoryHistory(),
            routes: [
                { path: '/', name: 'home', component: { template: '<div />' } },
                { path: '/library', name: 'library', component: { template: '<div />' } },
                { path: '/detail/:id', name: 'detail', component: { template: '<div />' } }
            ]
        })
        await router.push('/library?type=show&state=missing')
        await router.push('/detail/7')
        await router.isReady()
        // simulate the app router's afterEach tracker having recorded the
        // library entry (the route the user came from before the detail)
        previousRoute.value = {
            name: 'library',
            path: '/library',
            fullPath: '/library?type=show&state=missing',
            query: { type: 'show', state: 'missing' },
            params: {},
            hash: '',
            matched: [],
            meta: {},
            redirectedFrom: undefined
        } as RouteLocationNormalized

        const i18n = createI18n({ legacy: false, locale: 'en', messages: { en: {} } })
        const wrapper = mount(PbHead, {
            props: { backTo: { name: 'library' }, backLabel: 'Library', title: 'Detail' },
            global: { plugins: [router, i18n] }
        })

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()

        // history back restores the library entry WITH its query — a plain
        // push to {name:'library'} would have dropped type and state
        expect(router.currentRoute.value.name).toBe('library')
        expect(router.currentRoute.value.query.type).toBe('show')
        expect(router.currentRoute.value.query.state).toBe('missing')
    })

    it('pushes the back target when history has no qualifying previous entry (deep link)', async () => {
        previousRoute.value = null
        const { wrapper, router } = await mountHead({
            backTo: { name: 'queue' },
            backLabel: 'Queue',
            title: 'Detail'
        })

        expect(router.currentRoute.value.name).toBe('home')
        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('queue')
    })

    it('renders the actions slot so pages can mount their own action buttons', async () => {
        const { wrapper } = await mountHead({ title: 'Detail' })

        const action = wrapper.find('[data-test="slot-action"]')
        expect(action.exists()).toBe(true)
        expect(action.text()).toBe('Save')

        const handler = vi.fn()
        action.element.addEventListener('click', handler)
        await action.trigger('click')
        expect(handler).toHaveBeenCalledTimes(1)
    })
})
