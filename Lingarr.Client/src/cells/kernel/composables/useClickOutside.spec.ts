// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { defineComponent, h, ref } from 'vue'
import { mount } from '@vue/test-utils'
import useClickOutside from '@/cells/kernel/composables/useClickOutside'

/// The composable guards dropdown-style panels. Its listener must survive
/// intermediate stopPropagation (modal card bodies) — hence the capture
/// phase — and ignore clicks inside the panel and the exclude element.
function setup() {
    const outside = vi.fn()
    const panel = ref<HTMLElement | undefined>()
    const trigger = ref<HTMLElement | undefined>()

    const Host = defineComponent({
        setup() {
            useClickOutside(panel, outside, trigger)
            return () =>
                h('div', [
                    h('div', { ref: trigger, class: 'trigger' }, 'trigger'),
                    h('div', { ref: panel, class: 'panel' }, 'panel'),
                    // simulates a modal card that stops click propagation
                    h('div', { class: 'shield', onClick: (e: Event) => e.stopPropagation() }, [
                        h('span', { class: 'shield-inner' }, 'modal body')
                    ])
                ])
        }
    })

    const wrapper = mount(Host, { attachTo: document.body })
    return { outside, wrapper }
}

const click = (selector: string) => {
    document.querySelector(selector)!.dispatchEvent(
        new MouseEvent('click', { bubbles: true, composed: true })
    )
}

describe('useClickOutside', () => {
    it('fires the callback for a click outside the panel and trigger', async () => {
        const { outside, wrapper } = setup()
        await nextTick()
        click('.shield')
        expect(outside).toHaveBeenCalledOnce()
        wrapper.unmount()
    })

    it('ignores clicks inside the panel and inside the exclude element', async () => {
        const { outside, wrapper } = setup()
        await nextTick()
        click('.panel')
        click('.trigger')
        expect(outside).not.toHaveBeenCalled()
        wrapper.unmount()
    })

    it('still fires when an intermediate wrapper stops propagation (modal shield)', async () => {
        const { outside, wrapper } = setup()
        await nextTick()
        // the deep span bubbles into the shield which stops propagation —
        // a bubble-phase window listener would never see this click
        click('.shield-inner')
        expect(outside).toHaveBeenCalledOnce()
        wrapper.unmount()
    })

    it('removes the listener on unmount', async () => {
        const { outside, wrapper } = setup()
        await nextTick()
        wrapper.unmount()
        // the mounted DOM is gone; a body click (outside target) must not
        // reach the removed listener
        document.body.dispatchEvent(
            new MouseEvent('click', { bubbles: true, composed: true })
        )
        expect(outside).not.toHaveBeenCalled()
    })
})
