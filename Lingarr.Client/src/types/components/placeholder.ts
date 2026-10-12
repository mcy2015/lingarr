export interface IPlaceholder {
    placeholder: string
    placeholderText: string
    title: string
    description: string
}

export const PLACEHOLDER: Record<string, IPlaceholder> = {
    MODEL: {
        placeholder: '{model}',
        placeholderText: 'placeholder.model.insert',
        title: 'placeholder.model.title',
        description: 'placeholder.model.description'
    },
    SYSTEM_PROMPT: {
        placeholder: '{systemPrompt}',
        placeholderText: 'placeholder.systemPrompt.insert',
        title: 'placeholder.systemPrompt.title',
        description: 'placeholder.systemPrompt.description'
    },
    USER_MESSAGE: {
        placeholder: '{userMessage}',
        placeholderText: 'placeholder.userMessage.insert',
        title: 'placeholder.userMessage.title',
        description: 'placeholder.userMessage.description'
    },
    SOURCE_LANGUAGE: {
        placeholder: '{sourceLanguage}',
        placeholderText: 'placeholder.sourceLanguage.insert',
        title: 'placeholder.sourceLanguage.title',
        description: 'placeholder.sourceLanguage.description'
    },
    TARGET_LANGUAGE: {
        placeholder: '{targetLanguage}',
        placeholderText: 'placeholder.targetLanguage.insert',
        title: 'placeholder.targetLanguage.title',
        description: 'placeholder.targetLanguage.description'
    },
    LINE_TO_TRANSLATE: {
        placeholder: '{lineToTranslate}',
        placeholderText: 'placeholder.lineToTranslate.insert',
        title: 'placeholder.lineToTranslate.title',
        description: 'placeholder.lineToTranslate.description'
    },
    CONTEXT_BEFORE: {
        placeholder: '{contextBefore}',
        placeholderText: 'placeholder.contextBefore.insert',
        title: 'placeholder.contextBefore.title',
        description: 'placeholder.contextBefore.description'
    },
    CONTEXT_AFTER: {
        placeholder: '{contextAfter}',
        placeholderText: 'placeholder.contextAfter.insert',
        title: 'placeholder.contextAfter.title',
        description: 'placeholder.contextAfter.description'
    },
    SOURCE_LINE: {
        placeholder: '{sourceLine}',
        placeholderText: 'placeholder.sourceLine.insert',
        title: 'placeholder.sourceLine.title',
        description: 'placeholder.sourceLine.description'
    },
    TRANSLATED_LINE: {
        placeholder: '{translatedLine}',
        placeholderText: 'placeholder.translatedLine.insert',
        title: 'placeholder.translatedLine.title',
        description: 'placeholder.translatedLine.description'
    }
}
