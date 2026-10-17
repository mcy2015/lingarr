// @vitest-environment happy-dom
// SubtitleSourcesCard: GET /api/subtitle-search/providers 数据驱动的字幕源卡。
// 只渲染 type=secret 的设置字段(url 等字段本期不进 UI),secret 字段经
// DynamicPluginForm/PluginField 的加密通道写入;无 secret 字段的源是紧凑单行。
// 测试连接 UX 对齐 SettingsProvidersPage:testing 中态、成功行带延迟数值、
// 失败行(含 501 不支持探测)带 message,不升格为错误态。
import { beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import SubtitleSourcesCard from '@/cells/settings/components/SubtitleSourcesCard.vue'
import { IPluginSettingField, ISubtitleProviderHealthState } from '@/types'
import { useToastStore } from '@/cells/app/stores/toast'
import en from '@/i18n/locales/en.json'
// side effect: installs String.prototype.format used by InputComponent validation
import '@/cells/kernel/utils/string'

const getProvidersMock = vi.hoisted(() => vi.fn())
const testConnectionMock = vi.hoisted(() => vi.fn())
const setSettingMock = vi.hoisted(() => vi.fn())
const setEncryptedSettingMock = vi.hoisted(() => vi.fn())
const revealEncryptedSettingMock = vi.hoisted(() => vi.fn())
const getEncryptedSettingStatesMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/subtitleSearchService', () => ({
    subtitleSearchService: {
        getProviders: getProvidersMock,
        testConnection: testConnectionMock
    }
}))

vi.mock('@/cells/app/api/settingService', () => ({
    settingService: () => ({
        setSetting: setSettingMock,
        setSettings: vi.fn().mockResolvedValue(undefined),
        setEncryptedSetting: setEncryptedSettingMock,
        getSettings: vi.fn().mockResolvedValue({}),
        getEncryptedSettingStates: getEncryptedSettingStatesMock,
        revealEncryptedSetting: revealEncryptedSettingMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

// 三源样本:opensubtitles(required secret 未配置,混入 url 字段;type 用
// 契约示例的小写 'secret' 验证归一)、assrt(已配置的 'Secret' 源)、
// podnapisi(无 secret 字段的紧凑行源)。默认全部健康(cooldown 字段 null)。
const PROVIDERS: ISubtitleProviderHealthState[] = [
    {
        provider: 'opensubtitles',
        displayName: 'OpenSubtitles',
        description: 'Largest community subtitle archive.',
        requiresAuth: true,
        credentialsConfigured: false,
        settings: [
            {
                key: 'subtitle_opensubtitles_api_key',
                label: 'API key',
                // 契约示例的小写 'secret' 线上形态 —— 类型域外,双重断言只为
                // 在夹具里复现该线格式,验证卡片的归一逻辑
                type: 'secret' as unknown as IPluginSettingField['type'],
                required: true,
                description: null
            },
            {
                key: 'subtitle_opensubtitles_base_url',
                label: 'Base URL',
                type: 'Url',
                required: false,
                description: null
            }
        ],
        cooldownUntil: null,
        cooldownReason: null
    },
    {
        provider: 'assrt',
        displayName: 'ASSRT',
        description: 'Chinese subtitle hub.',
        requiresAuth: true,
        credentialsConfigured: true,
        settings: [
            {
                key: 'subtitle_assrt_token',
                label: 'API token',
                type: 'Secret',
                required: true,
                description: null
            }
        ],
        cooldownUntil: null,
        cooldownReason: null
    },
    {
        provider: 'podnapisi',
        displayName: 'Podnapisi',
        description: 'Indexing without an account.',
        requiresAuth: false,
        credentialsConfigured: true,
        settings: [
            {
                key: 'subtitle_podnapisi_base_url',
                label: 'Base URL',
                type: 'Url',
                required: false,
                description: null
            }
        ],
        cooldownUntil: null,
        cooldownReason: null
    }
]

beforeEach(() => {
    getProvidersMock.mockReset().mockResolvedValue(PROVIDERS)
    testConnectionMock.mockReset()
    setSettingMock.mockReset().mockResolvedValue(undefined)
    setEncryptedSettingMock.mockReset().mockResolvedValue(undefined)
    revealEncryptedSettingMock.mockReset().mockResolvedValue('')
    getEncryptedSettingStatesMock.mockReset().mockResolvedValue({})
})

async function mountCard() {
    const pinia = createPinia()
    const wrapper = mount(SubtitleSourcesCard, {
        global: { plugins: [pinia, i18n] }
    })
    onTestFinished(() => wrapper.unmount())
    await flushPromises()
    return wrapper
}

describe('SubtitleSourcesCard · 行渲染', () => {
    it('renders every provider row data-driven with the API description and count', async () => {
        const wrapper = await mountCard()

        expect(getProvidersMock).toHaveBeenCalledTimes(1)
        for (const source of PROVIDERS) {
            const row = wrapper.find(`[data-test="subtitle-source-row-${source.provider}"]`)
            expect(row.exists()).toBe(true)
            expect(row.text()).toContain(source.displayName)
            expect(row.text()).toContain(source.description as string)
        }
        expect(wrapper.find('[data-test="subtitle-sources-count"]').text()).toContain('3')
    })

    it('pins the name column to a fixed width so the enable switches align vertically (E6)', async () => {
        const wrapper = await mountCard()

        for (const source of PROVIDERS) {
            const name = wrapper.find(`[data-test="subtitle-source-row-${source.provider}"] b`)
            // fixed width column + truncate for overlong plugin names — the
            // switch that follows starts at the same x on every row
            expect(name.classes()).toContain('w-[11.5rem]')
            expect(name.classes()).toContain('truncate')
            expect(name.text()).toBe(source.displayName)
        }
    })

    it('renders no-secret sources as compact rows: no form, no expand, no badge', async () => {
        const wrapper = await mountCard()

        expect(wrapper.find('[data-test="subtitle-source-form-podnapisi"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="subtitle-source-toggle-podnapisi"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="subtitle-source-badge-podnapisi"]').exists()).toBe(false)
        // the url-only field never enters this round's UI
        expect(wrapper.find('[data-test="subtitle-source-row-podnapisi"]').text()).not.toContain(
            'Base URL'
        )
        // every source implements the health probe, so the test button stays
        expect(wrapper.find('[data-test="subtitle-source-test-podnapisi"]').exists()).toBe(true)
    })

    it('renders only the secret field inside the expandable form (url filtered out)', async () => {
        const wrapper = await mountCard()

        await wrapper.find('[data-test="subtitle-source-toggle-opensubtitles"]').trigger('click')
        await flushPromises()

        const form = wrapper.find('[data-test="subtitle-source-form-opensubtitles"]')
        expect(form.exists()).toBe(true)
        // lowercase 'secret' from the wire is normalized: password channel
        const secretInput = form.find('#subtitle_opensubtitles_api_key')
        expect(secretInput.exists()).toBe(true)
        expect(secretInput.attributes('type')).toBe('password')
        expect(form.find('#subtitle_opensubtitles_base_url').exists()).toBe(false)
        expect(form.text()).not.toContain('Base URL')

        // secret keys are loaded into the setting store for the reveal flow
        expect(getEncryptedSettingStatesMock).toHaveBeenCalledWith([
            'subtitle_opensubtitles_api_key',
            'subtitle_assrt_token'
        ])
    })

    it('toggles the form closed again', async () => {
        const wrapper = await mountCard()

        await wrapper.find('[data-test="subtitle-source-toggle-opensubtitles"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="subtitle-source-form-opensubtitles"]').exists()).toBe(true)

        await wrapper.find('[data-test="subtitle-source-toggle-opensubtitles"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="subtitle-source-form-opensubtitles"]').exists()).toBe(false)
    })
})

describe('SubtitleSourcesCard · 配置徽章', () => {
    it('shows notConfigured / configured per credentialsConfigured (secret sources only)', async () => {
        const wrapper = await mountCard()

        expect(wrapper.find('[data-test="subtitle-source-badge-opensubtitles"]').text()).toBe(
            en.settings.subtitleSources.notConfigured
        )
        expect(wrapper.find('[data-test="subtitle-source-badge-assrt"]').text()).toBe(
            en.settings.subtitleSources.configured
        )
    })
})

describe('SubtitleSourcesCard · 测试连接', () => {
    it('calls testConnection for the row provider and renders the success line with latency', async () => {
        testConnectionMock.mockResolvedValueOnce({ ok: true, latencyMs: 123.4, message: 'reachable' })
        const wrapper = await mountCard()

        await wrapper.find('[data-test="subtitle-source-test-podnapisi"]').trigger('click')
        await flushPromises()

        expect(testConnectionMock).toHaveBeenCalledWith('podnapisi')
        const line = wrapper.find('[data-test="subtitle-source-test-result-podnapisi"]')
        expect(line.exists()).toBe(true)
        expect(line.attributes('data-ok')).toBe('true')
        expect(line.text()).toContain(en.settings.subtitleSources.testOk)
        expect(line.text()).toContain('123 ms')
    })

    it('renders the failed line with the message for rejections (501 unsupported included)', async () => {
        testConnectionMock.mockRejectedValueOnce(new Error('provider does not support probing'))
        const wrapper = await mountCard()

        await wrapper.find('[data-test="subtitle-source-test-opensubtitles"]').trigger('click')
        await flushPromises()

        const line = wrapper.find('[data-test="subtitle-source-test-result-opensubtitles"]')
        expect(line.exists()).toBe(true)
        expect(line.attributes('data-ok')).toBe('false')
        expect(line.text()).toContain(en.settings.subtitleSources.testFailed)
        expect(line.text()).toContain('provider does not support probing')
        // failed probe stays an inline row, never an error state
        expect(wrapper.find('[data-test="subtitle-sources-error"]').exists()).toBe(false)
    })

    it('shows the testing middle state while the probe is in flight', async () => {
        let release: (value: { ok: boolean; latencyMs: number; message: string }) => void = () => {}
        testConnectionMock.mockReturnValueOnce(
            new Promise((resolve) => {
                release = resolve
            })
        )
        const wrapper = await mountCard()

        const button = wrapper.find('[data-test="subtitle-source-test-assrt"]')
        await button.trigger('click')
        await flushPromises()

        expect(button.attributes('disabled')).toBeDefined()
        expect(button.text()).toBe(en.settings.subtitleSources.testing)
        const line = wrapper.find('[data-test="subtitle-source-test-result-assrt"]')
        expect(line.text()).toBe(en.settings.subtitleSources.testing)

        release({ ok: true, latencyMs: 88, message: 'ok' })
        await flushPromises()
        expect(button.attributes('disabled')).toBeUndefined()
        expect(line.text()).toContain('88 ms')
    })
})

describe('SubtitleSourcesCard · 保存与刷新', () => {
    it('writes the secret through the encrypted channel and re-fetches providers so the badge flips', async () => {
        vi.useFakeTimers()
        try {
            const wrapper = await mountCard()

            await wrapper.find('[data-test="subtitle-source-toggle-opensubtitles"]').trigger('click')
            await flushPromises()
            getProvidersMock.mockClear()
            // next fetch (triggered by the save) reports the key as configured
            getProvidersMock.mockResolvedValueOnce([
                { ...PROVIDERS[0], credentialsConfigured: true },
                PROVIDERS[1],
                PROVIDERS[2]
            ])

            const input = wrapper.find('#subtitle_opensubtitles_api_key')
            await input.setValue('k'.repeat(32))
            await vi.advanceTimersByTimeAsync(1100)
            await flushPromises()

            expect(setEncryptedSettingMock).toHaveBeenCalledWith(
                'subtitle_opensubtitles_api_key',
                'k'.repeat(32)
            )
            expect(getProvidersMock).toHaveBeenCalledTimes(1)
            expect(wrapper.find('[data-test="subtitle-source-badge-opensubtitles"]').text()).toBe(
                en.settings.subtitleSources.configured
            )
            // the expanded form survives the refresh
            expect(wrapper.find('[data-test="subtitle-source-form-opensubtitles"]').exists()).toBe(
                true
            )
        } finally {
            vi.useRealTimers()
        }
    })
})

describe('SubtitleSourcesCard · 加载状态', () => {
    it('shows the retryable error row when the first load fails, then recovers', async () => {
        getProvidersMock
            .mockReset()
            .mockResolvedValue(PROVIDERS)
            .mockRejectedValueOnce(new Error('boom'))
        const wrapper = await mountCard()

        const error = wrapper.find('[data-test="subtitle-sources-error"]')
        expect(error.exists()).toBe(true)
        expect(wrapper.text()).toContain(en.settings.subtitleSources.loadFailed)
        expect(wrapper.find('[data-test="subtitle-source-row-opensubtitles"]').exists()).toBe(false)

        await wrapper.find('[data-test="subtitle-sources-retry"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="subtitle-sources-error"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="subtitle-source-row-opensubtitles"]').exists()).toBe(true)
    })

    it('renders an honest empty state when no sources are registered', async () => {
        getProvidersMock.mockReset().mockResolvedValue([])
        const wrapper = await mountCard()

        const empty = wrapper.find('[data-test="subtitle-sources-empty"]')
        expect(empty.exists()).toBe(true)
        expect(empty.text()).toBe(en.settings.subtitleSources.empty)
    })
})

describe('SubtitleSourcesCard · 冷却健康态(设计⑧)', () => {    it('renders the cooldown chip with the until time and reason tooltip on cooled sources only', async () => {
        const until = '2026-10-15T18:30:00Z'
        getProvidersMock.mockReset().mockResolvedValue([
            { ...PROVIDERS[0], cooldownUntil: until, cooldownReason: 'RateLimited' },
            PROVIDERS[1],
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        const chip = wrapper.find('[data-test="subtitle-source-cooldown-opensubtitles"]')
        expect(chip.exists()).toBe(true)
        // chip text: 「Cooldown · <localized datetime>」— the template's
        // label prefix with a real interpolated time (no leftover
        // placeholder, and text beyond the bare label)
        const template = en.settings.subtitleSources.cooldownUntil
        expect(chip.text()).toContain(template.split('{time}')[0])
        expect(chip.text()).not.toContain('{time}')
        expect(chip.text().length).toBeGreaterThan(template.replace('{time}', '').length)
        // reason rides the tooltip (bucketed failure name, localized)
        expect(chip.attributes('title')).toBe(en.settings.subtitleSources.cooldownReason.RateLimited)

        // healthy sources render no chip at all
        expect(wrapper.find('[data-test="subtitle-source-cooldown-assrt"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="subtitle-source-cooldown-podnapisi"]').exists()).toBe(false)
    })

    it('renders the until-credentials label for the 401/403 sentinel instead of a year-9999 date', async () => {
        getProvidersMock.mockReset().mockResolvedValue([
            PROVIDERS[0],
            {
                ...PROVIDERS[1],
                cooldownUntil: '9999-12-31T23:59:59+00:00',
                cooldownReason: 'Unauthorized'
            },
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        const chip = wrapper.find('[data-test="subtitle-source-cooldown-assrt"]')
        expect(chip.exists()).toBe(true)
        expect(chip.text()).toBe(en.settings.subtitleSources.cooldownUntilCredentials)
        expect(chip.attributes('title')).toBe(en.settings.subtitleSources.cooldownReason.Unauthorized)
        // the far-future timestamp itself never reaches the user
        expect(chip.text()).not.toContain('9999')
    })

    it('shows no chip for legacy payloads without the cooldown fields', async () => {
        getProvidersMock.mockReset().mockResolvedValue([
            { ...PROVIDERS[0], cooldownUntil: undefined, cooldownReason: undefined },
            PROVIDERS[1],
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        expect(wrapper.find('[data-test="subtitle-source-cooldown-opensubtitles"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="subtitle-source-row-opensubtitles"]').exists()).toBe(true)
    })

    it('labels the DailyLimit cooldown reason (daily quota exhausted)', async () => {
        getProvidersMock.mockReset().mockResolvedValue([
            { ...PROVIDERS[0], cooldownUntil: '2026-10-15T23:59:00Z', cooldownReason: 'DailyLimit' },
            PROVIDERS[1],
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        const chip = wrapper.find('[data-test="subtitle-source-cooldown-opensubtitles"]')
        expect(chip.exists()).toBe(true)
        // known reason names resolve through the label map — never the raw
        // enum name on screen
        expect(chip.attributes('title')).toBe(en.settings.subtitleSources.cooldownReason.DailyLimit)
        expect(chip.text()).not.toContain('DailyLimit')
    })
})

describe('SubtitleSourcesCard · 启用开关(设计⑧打磨)', () => {
    it('renders the enable switch per row reflecting the API state', async () => {
        getProvidersMock.mockReset().mockResolvedValue([
            PROVIDERS[0],
            { ...PROVIDERS[1], enabled: false },
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        const enabledSwitch = wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"]')
        expect(enabledSwitch.exists()).toBe(true)
        expect(enabledSwitch.text()).toBe(en.settings.subtitleSources.enabled)
        expect(enabledSwitch.find('button').attributes('aria-checked')).toBe('true')

        const disabledSwitch = wrapper.find('[data-test="subtitle-source-enabled-assrt"]')
        expect(disabledSwitch.text()).toBe(en.settings.subtitleSources.disabled)
        expect(disabledSwitch.find('button').attributes('aria-checked')).toBe('false')
    })

    it('treats legacy payloads without the enabled field as enabled', async () => {
        getProvidersMock.mockReset().mockResolvedValue([
            { ...PROVIDERS[0], enabled: undefined },
            PROVIDERS[1],
            PROVIDERS[2]
        ])
        const wrapper = await mountCard()

        const legacySwitch = wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"] button')
        expect(legacySwitch.attributes('aria-checked')).toBe('true')
        expect(wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"]').text()).toBe(
            en.settings.subtitleSources.enabled
        )
    })

    it('writes subtitle_{provider}_enabled through the setting channel and flips the label', async () => {
        const wrapper = await mountCard()
        setSettingMock.mockClear()

        await wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"] button').trigger('click')
        await flushPromises()

        // The card writes the plain setting key with the string wire form.
        expect(setSettingMock).toHaveBeenCalledTimes(1)
        expect(setSettingMock).toHaveBeenCalledWith('subtitle_opensubtitles_enabled', 'false')
        expect(wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"]').text()).toBe(
            en.settings.subtitleSources.disabled
        )

        // toggling back on writes "true" and restores the label
        await wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"] button').trigger('click')
        await flushPromises()
        expect(setSettingMock).toHaveBeenCalledWith('subtitle_opensubtitles_enabled', 'true')
        expect(wrapper.find('[data-test="subtitle-source-enabled-opensubtitles"]').text()).toBe(
            en.settings.subtitleSources.enabled
        )
    })

    it('keeps the previous state and toasts an error when the write fails', async () => {
        setSettingMock.mockReset().mockRejectedValueOnce(new Error('settings down'))
        // local pinia so the toast the card pushes can be asserted
        const pinia = createPinia()
        const wrapper = mount(SubtitleSourcesCard, {
            global: { plugins: [pinia, i18n] }
        })
        onTestFinished(() => wrapper.unmount())
        await flushPromises()

        await wrapper.find('[data-test="subtitle-source-enabled-assrt"] button').trigger('click')
        await flushPromises()

        expect(setSettingMock).toHaveBeenCalledWith('subtitle_assrt_enabled', 'false')
        // state kept: still enabled
        expect(wrapper.find('[data-test="subtitle-source-enabled-assrt"]').text()).toBe(
            en.settings.subtitleSources.enabled
        )
        // the error surfaces through the toast store (real pinia)
        const toastStore = useToastStore(pinia)
        const lastToast = toastStore.toasts[toastStore.toasts.length - 1]
        expect(lastToast?.type).toBe('error')
        expect(lastToast?.message).toBe(en.settings.subtitleSources.toggleFailed)
    })
})
