// @vitest-environment happy-dom
// Walk-through fix D5: the description paragraph must disappear (together
// with its mb-6 gutter) when the slot carries no content — title-less stat
// containers on the drill-down pages were left with a stray gap.
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import CardComponent from '@/cells/kernel/components/CardComponent.vue'

describe('CardComponent', () => {
    it('renders the description paragraph when the slot has content', () => {
        const wrapper = mount(CardComponent, {
            slots: { description: 'Explains the card' }
        })

        const paragraph = wrapper.find('p.mb-6')
        expect(paragraph.exists()).toBe(true)
        expect(paragraph.text()).toBe('Explains the card')
    })

    it('omits the description paragraph when the slot is absent', () => {
        const wrapper = mount(CardComponent)

        expect(wrapper.find('p').exists()).toBe(false)
        expect(wrapper.find('p.mb-6').exists()).toBe(false)
    })

    it('omits the description paragraph for a whitespace-only slot', () => {
        const wrapper = mount(CardComponent, {
            slots: { description: '   ' }
        })

        expect(wrapper.find('p').exists()).toBe(false)
    })

    it('omits the description paragraph when an interpolation renders empty', () => {
        // dynamic descriptions (e.g. a missing i18n value) must not hold the
        // gutter open either
        const wrapper = mount(CardComponent, {
            slots: { description: '{{ "" }}' }
        })

        expect(wrapper.find('p').exists()).toBe(false)
    })

    it('still renders the optional title and the content slot', () => {
        const wrapper = mount(CardComponent, {
            props: { title: 'Stats' },
            slots: { content: '<div data-test="inner" />' }
        })

        expect(wrapper.find('h2').text()).toBe('Stats')
        expect(wrapper.find('[data-test="inner"]').exists()).toBe(true)
    })

    it('renders no title heading when the title prop is absent', () => {
        const wrapper = mount(CardComponent, {
            slots: { content: 'body' }
        })

        expect(wrapper.find('h2').exists()).toBe(false)
    })
})
