// @vitest-environment happy-dom
// Drill-down page skeleton: the shared root every drill-down screen mounts
// on. Frozen container metrics (measured from the approved schedule screen):
// the dd-page root carries the main-nav content padding (p-4 lg:p-6) plus
// the head-to-content and block-to-block gap (gap-3.5); the dd-content area
// carries the same gap token so "same class = same token" makes the spacing
// uniform structurally, not per-page hand-tuning.
import { describe, expect, it } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { mount } from '@vue/test-utils'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'

type SkeletonProps = InstanceType<typeof DrillDownPage>['$props']

const mountSkeleton = async (props: SkeletonProps = { title: 'Detail' }) => {
    const router = createRouter({
        history: createMemoryHistory(),
        routes: [
            { path: '/', name: 'home', component: { template: '<div />' } },
            { path: '/queue', name: 'queue', component: { template: '<div />' } }
        ]
    })
    await router.push('/')
    await router.isReady()

    return mount(DrillDownPage, {
        props,
        slots: {
            default: '<div data-test="slot-body">body</div>',
            actions: '<button data-test="slot-action" type="button">Save</button>'
        },
        global: { plugins: [router] }
    })
}

describe('DrillDownPage (drill-down skeleton)', () => {
    it('passes the head props through to PbHead and renders the actions slot', async () => {
        const wrapper = await mountSkeleton({
            backTo: { name: 'queue' },
            backLabel: 'Queue',
            title: 'Shōgun — S01E05',
            sub: '#2417 · EN → ZH'
        })

        expect(wrapper.find('[data-test="pb-back"]').text()).toContain('Queue')
        expect(wrapper.find('b.truncate').text()).toBe('Shōgun — S01E05')
        expect(wrapper.find('div.mono').text()).toBe('#2417 · EN → ZH')
        expect(wrapper.find('[data-test="slot-action"]').exists()).toBe(true)
    })

    it('carries the frozen skeleton metrics on the dd-page root and dd-content area', async () => {
        const wrapper = await mountSkeleton({ title: 'Detail' })

        // dd-page root: main-nav content margins + head/block gap, clipped
        // viewport (min-h-0 column) — the schedule-screen baseline tokens.
        const root = wrapper.find('[data-test="dd-page"]')
        expect(root.classes()).toEqual([
            'w-full',
            'flex',
            'h-full',
            'min-h-0',
            'flex-col',
            'gap-3.5',
            'p-4',
            'lg:p-6',
            'overflow-hidden'
        ])

        // dd-content: the unified content area — same gap token, fills the
        // remaining height; page content lands inside it through the slot.
        const content = wrapper.find('[data-test="dd-content"]')
        expect(content.classes()).toEqual([
            'flex',
            'min-h-0',
            'flex-1',
            'flex-col',
            'gap-3.5'
        ])
        expect(content.find('[data-test="slot-body"]').exists()).toBe(true)
        expect(root.find('[data-test="slot-body"]').exists()).toBe(true)
    })

    it('switches the root to the scrolling column for long card-flow pages', async () => {
        const wrapper = await mountSkeleton({ title: 'Authentication', scrollable: true })

        const classes = wrapper.find('[data-test="dd-page"]').classes()
        expect(classes).toContain('shell-scroll')
        expect(classes).toContain('overflow-y-auto')
        expect(classes).not.toContain('overflow-hidden')
    })
})
