// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { createMemoryHistory, createRouter } from 'vue-router'
import { flushPromises, mount, VueWrapper } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import RequestTemplatePage from '@/cells/settings/pages/RequestTemplatePage.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import { useUiStore } from '@/cells/app/stores/ui'
import { SETTINGS } from '@/types'

const listPluginsMock = vi.hoisted(() => vi.fn())
const getDefaultsMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/settings/services/pluginService', () => ({
    pluginService: () => ({ list: listPluginsMock })
}))

vi.mock('@/cells/settings/services/requestTemplateService', () => ({
    requestTemplateService: () => ({ getDefaults: getDefaultsMock })
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({ setSetting: setSettingMock })
}))

const CUSTOM_TEMPLATE = '{"model":"{model}","temperature":0.2}'
const DEFAULT_TEMPLATE =
    '{"model":"{model}","messages":[{"role":"system","content":"{systemPrompt}"}]}'

async function mountPage(service = 'openai'): Promise<{
    wrapper: VueWrapper
    settingStore: ReturnType<typeof useSettingStore>
    toastStore: ReturnType<typeof useToastStore>
    uiStore: ReturnType<typeof useUiStore>
    router: ReturnType<typeof createRouter>
}> {
    listPluginsMock.mockResolvedValue([
        {
            provider: 'openai',
            displayName: 'OpenAI',
            isBuiltIn: true,
            hasRequestTemplate: true
        },
        {
            provider: 'deepl',
            displayName: 'DeepL',
            isBuiltIn: true,
            hasRequestTemplate: false
        }
    ])
    getDefaultsMock.mockResolvedValue({ openai_request_template: DEFAULT_TEMPLATE })
    setSettingMock.mockResolvedValue(undefined)

    const pinia = createPinia()
    const settingStore = useSettingStore(pinia)
    settingStore.storeSetting(SETTINGS.SERVICE_TYPE, JSON.stringify(['openai', 'deepl']))
    settingStore.storeSetting(SETTINGS.OPENAI_MODEL, 'gpt-4o-mini')
    settingStore.storeSetting(SETTINGS.USE_BATCH_TRANSLATION, 'true')
    settingStore.storeSetting(SETTINGS.MAX_BATCH_SIZE, '128')
    settingStore.storeSetting(SETTINGS.OPENAI_REQUEST_TEMPLATE, CUSTOM_TEMPLATE)

    const i18n = createI18n({
        legacy: false,
        locale: 'en',
        messages: { en },
        missingWarn: false,
        fallbackWarn: false,
        warnHtmlMessage: false
    })

    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            {
                path: '/request-template/:service',
                name: 'request-template',
                component: RequestTemplatePage,
                props: true
            },
            {
                path: '/settings/providers',
                name: 'settings-providers',
                component: { template: '<div />' }
            }
        ]
    })
    await router.push(`/request-template/${service}`)
    await router.isReady()

    const wrapper = mount(RequestTemplatePage, {
        props: { service },
        global: {
            plugins: [pinia, i18n, router],
            stubs: {
                // independent cards, not part of this spec
                TranslationPrompt: true,
                ProofreadPrompt: true,
                TranslationPreviewCard: true
            }
        }
    })
    await flushPromises()

    return {
        wrapper,
        settingStore,
        toastStore: useToastStore(pinia),
        uiStore: useUiStore(pinia),
        router
    }
}

describe('RequestTemplatePage (drill-down, prototype screen-10)', () => {
    it('roots on the shared DrillDownPage skeleton (dd-page)', async () => {
        const { wrapper } = await mountPage()

        const skeleton = wrapper.findComponent(DrillDownPage)
        expect(skeleton.exists()).toBe(true)
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.exists()).toBe(true)
        // root-ness: the skeleton div is the first element under the mount point
        expect([...(root.element.parentElement?.children ?? [])].indexOf(root.element)).toBe(0)
    })

    it('renders the pb-head: back to providers, service title and the settings-derived mono sub', async () => {
        const { wrapper } = await mountPage()

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain(
            en.settings.menu.groups.providers
        )
        const title = wrapper.find('b.truncate')
        expect(title.text()).toBe(`${en.requestTemplate.title} · OpenAI`)
        // model + batch size from the settings store; temperature/glossary
        // have no setting and are omitted
        expect(wrapper.find('.mono.text-secondary-content').text()).toBe(
            `gpt-4o-mini · ${en.requestTemplate.subBatch.replace('{count}', '128')}`
        )
    })

    it('navigates back to the providers panel through the pb-head back button', async () => {
        const { wrapper, router } = await mountPage()

        await wrapper.find('[data-test="pb-back"]').trigger('click')
        await flushPromises()

        expect(router.currentRoute.value.name).toBe('settings-providers')
    })

    it('saves through the pb-head primary action with the new-batch toast (same action as the CTA)', async () => {
        const { wrapper, toastStore, uiStore } = await mountPage()

        expect(uiStore.cta?.label).toEqual({ key: 'requestTemplate.save' })

        await wrapper.find('[data-test="tpl-save"]').trigger('click')
        await flushPromises()

        expect(toastStore.toasts.at(-1)?.message).toBe(en.requestTemplate.savedToast)
        // the CTA handler drives the same action
        await uiStore.cta?.handler()
        expect(toastStore.toasts.at(-1)?.message).toBe(en.requestTemplate.savedToast)
    })

    it('resets the template to the provider default through the pb-head secondary action', async () => {
        const { wrapper, settingStore, toastStore } = await mountPage()

        const reset = wrapper.find('[data-test="tpl-reset"]')
        expect(reset.attributes('disabled')).toBeUndefined() // defaults loaded

        await reset.trigger('click')
        await flushPromises()

        expect(settingStore.getSetting(SETTINGS.OPENAI_REQUEST_TEMPLATE)).toBe(DEFAULT_TEMPLATE)
        expect(toastStore.toasts.at(-1)?.message).toBe(en.requestTemplate.resetToast)
    })

    it('clears the CTA and sub override on unmount', async () => {
        const { wrapper, uiStore } = await mountPage()

        expect(uiStore.cta).not.toBeNull()
        expect(uiStore.subOverride).toContain('OpenAI')

        wrapper.unmount()

        expect(uiStore.cta).toBeNull()
        expect(uiStore.subOverride).toBe('')
    })
})
