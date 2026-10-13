export default {
    mounted(el: HTMLElement) {
        const text = el.textContent
        const originalClasses = el.className
        if (!text) return

        const parts = text.split(' - ')

        if (parts.length !== 3) {
            return
        }
        const [showName, episodeNumber, episodeTitle] = parts

        // Build the layout with DOM APIs instead of innerHTML: episode
        // titles come from Sonarr and must never be parsed as HTML.
        const wrapper = document.createElement('div')
        wrapper.className = `${originalClasses} inline-flex items-center gap-1.5 overflow-hidden min-w-0 w-full`

        const appendSpan = (content: string, classes?: string) => {
            const span = document.createElement('span')
            if (classes) span.className = classes
            span.textContent = content
            wrapper.appendChild(span)
        }

        appendSpan(showName, 'shrink-0')
        appendSpan('-')
        appendSpan(episodeNumber)
        appendSpan('-')
        appendSpan(episodeTitle, 'text-primary-content/50 truncate block')

        el.textContent = ''
        el.appendChild(wrapper)
    }
}
