// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount } from '@vue/test-utils'
import JobRow from '@/cells/app/components/JobRow.vue'
import en from '@/i18n/locales/en.json'
import { ITranslationRequest, TRANSLATION_STATUS } from '@/types'

const request = {
    id: 2418,
    jobId: 'job-1',
    title: 'Dune: Part Two (2024)',
    sourceLanguage: 'en',
    targetLanguage: 'zh',
    mediaType: 'Movie',
    status: TRANSLATION_STATUS.INPROGRESS,
    progress: 38,
    serviceType: 'deepl',
    startedAt: new Date(Date.now() - 20 * 60_000).toISOString()
} as ITranslationRequest

const mountRow = async (item: ITranslationRequest = request) => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            { path: '/queue/:id', name: 'queue-detail', component: { template: '<div />' }, props: true }
        ]
    })
    await router.push('/')
    await router.isReady()

    const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
    const wrapper = mount(JobRow, {
        props: { request: item },
        global: { plugins: [router, i18n] }
    })
    return { wrapper, router }
}

describe('JobRow (shared active-task row)', () => {
    it('renders the title, language-pair sub line, service and percentage', async () => {
        const { wrapper } = await mountRow()

        expect(wrapper.find('[data-test="jobrow-2418"]').exists()).toBe(true)
        expect(wrapper.text()).toContain('Dune: Part Two (2024)')
        expect(wrapper.text()).toContain('EN → ZH')
        expect(wrapper.text()).toContain('deepl')
        expect(wrapper.text()).toContain('38%')
    })

    it('links the whole row to the queue-detail screen of the request', async () => {
        const { wrapper, router } = await mountRow()

        const link = wrapper.find('[data-test="jobrow-2418"]')
        expect(link.element.tagName).toBe('A')

        await link.trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('queue-detail')
        expect(router.currentRoute.value.params.id).toBe('2418')
    })

    it('falls back to the bare percentage when no ETA is derivable', async () => {
        const { wrapper } = await mountRow({
            ...request,
            id: 9,
            startedAt: null,
            progress: 0
        })

        const right = wrapper.find('.mono.text-right')
        expect(right.text()).toBe('0%')
    })
})
