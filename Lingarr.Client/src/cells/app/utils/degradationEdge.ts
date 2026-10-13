/// The server persists degradation edges as "from -> to" (ASCII, see
/// SubtitleTranslationService); the UI renders them with the typographic
/// arrow used everywhere else. Shared by the queue event timeline and the
/// overview activity feed (QA P3-4).
export function formatDegradationEdge(message: string): string {
    return message.replace(/ -> /g, ' → ')
}
