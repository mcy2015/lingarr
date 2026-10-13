// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { type RouteLocationRaw } from 'vue-router'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import PbHead from '@/cells/app/components/PbHead.vue'

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
