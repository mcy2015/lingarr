// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { useProofreadKeyboard } from '@/composables/useProofreadKeyboard'

type Handlers = Parameters<typeof useProofreadKeyboard>[0]

const makeHandlers = (): Handlers => ({
    next: vi.fn(),
    prev: vi.fn(),
    approve: vi.fn(),
    edit: vi.fn()
})

const pressOnWindow = (key: string): KeyboardEvent => {
    const event = new KeyboardEvent('keydown', { key, cancelable: true })
    window.dispatchEvent(event)
    return event
}

const mountFan = (handlers: Handlers) => {
    const Fan = defineComponent({
        setup() {
            useProofreadKeyboard(handlers)
            return () => h('div')
        }
    })
    return mount(Fan)
}

describe('useProofreadKeyboard', () => {
    it('maps j/k/enter/e to next/prev/approve/edit exactly once', () => {
        const handlers = makeHandlers()
        const wrapper = mountFan(handlers)

        pressOnWindow('j')
        pressOnWindow('k')
        pressOnWindow('Enter')
        pressOnWindow('e')

        expect(handlers.next).toHaveBeenCalledTimes(1)
        expect(handlers.prev).toHaveBeenCalledTimes(1)
        expect(handlers.approve).toHaveBeenCalledTimes(1)
        expect(handlers.edit).toHaveBeenCalledTimes(1)
        wrapper.unmount()
    })

    it('uppercase J also moves to the next line', () => {
        const handlers = makeHandlers()
        const wrapper = mountFan(handlers)

        pressOnWindow('J')

        expect(handlers.next).toHaveBeenCalledTimes(1)
        expect(handlers.prev).not.toHaveBeenCalled()
        expect(handlers.approve).not.toHaveBeenCalled()
        expect(handlers.edit).not.toHaveBeenCalled()
        wrapper.unmount()
    })

    it('prevents the default action for handled keys and ignores others', () => {
        const handlers = makeHandlers()
        const wrapper = mountFan(handlers)

        const handled = pressOnWindow('j')
        expect(handled.defaultPrevented).toBe(true)

        const ignored = pressOnWindow('x')
        expect(ignored.defaultPrevented).toBe(false)
        expect(handlers.next).toHaveBeenCalledTimes(1)
        expect(handlers.prev).not.toHaveBeenCalled()
        expect(handlers.approve).not.toHaveBeenCalled()
        expect(handlers.edit).not.toHaveBeenCalled()
        wrapper.unmount()
    })

    it('swallows every key while focus is inside an input', () => {
        const handlers = makeHandlers()
        const wrapper = mountFan(handlers)

        const input = document.createElement('input')
        document.body.append(input)
        const event = new KeyboardEvent('keydown', { key: 'j', bubbles: true, cancelable: true })
        input.dispatchEvent(event)
        input.remove()

        expect(handlers.next).not.toHaveBeenCalled()
        expect(handlers.prev).not.toHaveBeenCalled()
        expect(handlers.approve).not.toHaveBeenCalled()
        expect(handlers.edit).not.toHaveBeenCalled()
        wrapper.unmount()
    })

    it('stops handling keys after the host component unmounts', () => {
        const handlers = makeHandlers()
        const wrapper = mountFan(handlers)

        pressOnWindow('j')
        expect(handlers.next).toHaveBeenCalledTimes(1)

        wrapper.unmount()
        pressOnWindow('j')
        expect(handlers.next).toHaveBeenCalledTimes(1)
    })
})
