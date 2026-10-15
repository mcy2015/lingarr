// @vitest-environment happy-dom
// NormalizeTab (导入 · 归整 tab, F3): preview diff → two-step confirm →
// execute. Service layer fully mocked; the assertions walk the real
// interaction flow (select media → preview → arm → confirm → result).
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { flushPromises, mount } from '@vue/test-utils'
import en from '@/i18n/locales/en.json'
import NormalizeTab from '@/cells/import/components/NormalizeTab.vue'

const moviesMock = vi.hoisted(() => vi.fn())
const showsMock = vi.hoisted(() => vi.fn())
const normalizePreviewMock = vi.hoisted(() => vi.fn())
const normalizeExecuteMock = vi.hoisted(() => vi.fn())

vi.mock('@/cells/app/api/mediaService', () => ({
    mediaService: () => ({
        movies: moviesMock,
        shows: showsMock
    })
}))

vi.mock('@/cells/import/services/importService', () => ({
    importService: () => ({
        normalizePreview: normalizePreviewMock,
        normalizeExecute: normalizeExecuteMock
    })
}))

const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false
})

const PREVIEW = {
    items: [
        {
            oldPath: '/media/dune/Dune.2021.chs.srt',
            newPath: '/media/dune/Dune.2021.zh.srt',
            oldName: 'Dune.2021.chs.srt',
            newName: 'Dune.2021.zh.srt',
            language: 'zh',
            mediaTitle: 'Dune'
        },
        {
            oldPath: '/media/dune/Dune.2021.cht.ass',
            newPath: '/media/dune/Dune.2021.zh-TW.ass',
            oldName: 'Dune.2021.cht.ass',
            newName: 'Dune.2021.zh-TW.ass',
            language: 'zh-TW',
            mediaTitle: 'Dune'
        }
    ],
    skipped: [
        { path: '/media/dune/Dune.2021.scjp.srt', reason: 'dualLanguage' },
        { path: '/media/dune/Dune.2021.zh.srt', reason: 'alreadyNormalized' }
    ],
    writable: true,
    error: null
}

function mountTab() {
    return mount(NormalizeTab, {
        global: { plugins: [createPinia(), i18n] }
    })
}

async function mountWithPreview(preview: unknown = PREVIEW) {
    normalizePreviewMock.mockResolvedValue(preview)
    const wrapper = mountTab()
    await flushPromises()
    await wrapper.find('[data-test="normalize-media-select"]').setValue('1')
    await wrapper.find('[data-test="normalize-preview-btn"]').trigger('click')
    await flushPromises()
    return wrapper
}

beforeEach(() => {
    moviesMock.mockReset().mockResolvedValue({
        items: [
            { id: 1, title: 'Dune' },
            { id: 2, title: 'Blade Runner 2049' }
        ]
    })
    showsMock.mockReset().mockResolvedValue({
        items: [{ id: 5, title: 'Shōgun' }]
    })
    normalizePreviewMock.mockReset()
    normalizeExecuteMock.mockReset()
})

describe('NormalizeTab · media picker', () => {
    it('loads movie options on mount and keeps preview disabled until one is picked', async () => {
        const wrapper = mountTab()
        await flushPromises()

        expect(moviesMock).toHaveBeenCalledWith(1, '', 'title', true)
        const select = wrapper.find('[data-test="normalize-media-select"]')
        expect(select.findAll('option')).toHaveLength(3) // placeholder + 2 movies
        expect(
            wrapper.find('[data-test="normalize-preview-btn"]').attributes('disabled')
        ).toBeDefined()
        expect(wrapper.find('[data-test="normalize-item-row"]').exists()).toBe(false)
    })

    it('reloads show options when switching the media kind', async () => {
        const wrapper = mountTab()
        await flushPromises()

        await wrapper
            .findAll('button')
            .find((b) => b.text() === 'Show')!
            .trigger('click')
        await flushPromises()

        expect(showsMock).toHaveBeenCalledWith(1, '', 'title', true)
        const select = wrapper.find('[data-test="normalize-media-select"]')
        expect(select.findAll('option')).toHaveLength(2) // placeholder + 1 show
    })
})

describe('NormalizeTab · preview diff', () => {
    it('renders one row per planned rename with the language chip and the skipped files with reasons', async () => {
        const wrapper = await mountWithPreview()

        expect(normalizePreviewMock).toHaveBeenCalledWith('Movie', 1)
        const rows = wrapper.findAll('[data-test="normalize-item-row"]')
        expect(rows).toHaveLength(2)
        expect(rows[0].text()).toContain('Dune.2021.chs.srt')
        expect(rows[0].text()).toContain('Dune.2021.zh.srt')
        expect(rows[0].text()).toContain('zh')
        expect(rows[1].text()).toContain('Dune.2021.zh-TW.ass')

        const skips = wrapper.findAll('[data-test="normalize-skip-row"]')
        expect(skips).toHaveLength(2)
        expect(skips[0].text()).toContain('Dune.2021.scjp.srt')
        expect(skips[0].text()).toContain(en.import.normalize.reason.dualLanguage)
        expect(skips[1].text()).toContain(en.import.normalize.reason.alreadyNormalized)
    })

    it('shows the read-only error state instead of a table', async () => {
        const wrapper = await mountWithPreview({
            items: [],
            skipped: [],
            writable: false,
            error: 'readOnly'
        })

        expect(wrapper.find('[data-test="normalize-error"]').text()).toBe(
            en.import.normalize.readOnly
        )
        expect(wrapper.find('[data-test="normalize-item-row"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="normalize-confirm"]').exists()).toBe(false)
    })

    it('shows the missing-directory error state', async () => {
        const wrapper = await mountWithPreview({
            items: [],
            skipped: [],
            writable: false,
            error: 'missingDirectory'
        })

        expect(wrapper.find('[data-test="normalize-error"]').text()).toBe(
            en.import.normalize.missingDirectory
        )
    })

    it('shows the no-subtitles empty state', async () => {
        const wrapper = await mountWithPreview({
            items: [],
            skipped: [],
            writable: true,
            error: null
        })

        expect(wrapper.text()).toContain(en.import.normalize.empty)
        expect(wrapper.find('[data-test="normalize-confirm"]').exists()).toBe(false)
    })

    it('shows the all-skipped empty state when nothing is renamable', async () => {
        const wrapper = await mountWithPreview({
            items: [],
            skipped: [{ path: '/m/a.scjp.srt', reason: 'dualLanguage' }],
            writable: true,
            error: null
        })

        expect(wrapper.text()).toContain(en.import.normalize.nothing)
        expect(wrapper.find('[data-test="normalize-skip-row"]').exists()).toBe(true)
    })
})

describe('NormalizeTab · two-step confirm → execute', () => {
    it('arms on the first click and executes on the second with the previewed items', async () => {
        normalizeExecuteMock.mockResolvedValue({ renamed: 2, failed: [] })
        const wrapper = await mountWithPreview()

        const confirm = wrapper.find('[data-test="normalize-confirm"]')
        expect(confirm.text()).toBe(en.import.normalize.confirm.replace('{count}', '2'))

        // First click: arm only — the copy switches, nothing was sent.
        await confirm.trigger('click')
        await flushPromises()
        expect(normalizeExecuteMock).not.toHaveBeenCalled()
        expect(wrapper.find('[data-test="normalize-confirm"]').text()).toBe(
            en.import.normalize.confirmAgain
        )
        expect(wrapper.text()).toContain(en.import.normalize.confirmHint)

        // Cancel path returns to the unarmed copy.
        await wrapper
            .findAll('button')
            .find((b) => b.text() === en.import.normalize.cancel)!
            .trigger('click')
        expect(wrapper.find('[data-test="normalize-confirm"]').text()).toBe(
            en.import.normalize.confirm.replace('{count}', '2')
        )

        // Armed again, then confirmed: execute receives the mapped items.
        await wrapper.find('[data-test="normalize-confirm"]').trigger('click')
        await wrapper.find('[data-test="normalize-confirm"]').trigger('click')
        await flushPromises()

        expect(normalizeExecuteMock).toHaveBeenCalledWith({
            mediaType: 'Movie',
            mediaId: 1,
            items: PREVIEW.items.map((item) => ({
                oldPath: item.oldPath,
                newPath: item.newPath,
                language: item.language,
                mediaTitle: item.mediaTitle
            }))
        })
        expect(wrapper.find('[data-test="normalize-result"]').text()).toContain(
            en.import.normalize.result.replace('{count}', '2')
        )
        // The consumed preview leaves the screen — no double execution.
        expect(wrapper.find('[data-test="normalize-item-row"]').exists()).toBe(false)
    })

    it('lists per-file failures after execution', async () => {
        normalizeExecuteMock.mockResolvedValue({
            renamed: 1,
            failed: [{ path: '/media/dune/Dune.2021.cht.ass', error: 'target already exists' }]
        })
        const wrapper = await mountWithPreview()

        await wrapper.find('[data-test="normalize-confirm"]').trigger('click')
        await wrapper.find('[data-test="normalize-confirm"]').trigger('click')
        await flushPromises()

        const result = wrapper.find('[data-test="normalize-result"]')
        expect(result.text()).toContain(en.import.normalize.result.replace('{count}', '1'))
        expect(result.text()).toContain('Dune.2021.cht.ass')
        expect(result.text()).toContain('target already exists')
    })
})
