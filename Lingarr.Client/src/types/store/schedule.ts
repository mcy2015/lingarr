export interface IUseScheduleStore {
    recurringJobs: IRecurringJob[]
    queueDepths: IQueueDepth[]
    timeZone: IScheduleTimeZone | null
}

/// Effective schedule time zone (GET /api/schedule/time-zone): crons are
/// interpreted in it and schedule times rendered in it. warning is set when
/// the server TZ is unset/invalid and the zone fell back to UTC.
export interface IScheduleTimeZone {
    ianaId: string
    warning: string | null
}

export interface IRunningJob {
    path: string
    language: string
    fileName: string
    jobId: string
}

export interface IRecurringJob {
    id: string
    cron: string
    queue: string
    jobMethod: string
    nextExecution: Date | null
    lastJobId: string | null
    lastJobState: string | null
    lastExecution: Date | null
    createdAt: Date | null
    timeZoneId: string | null
    currentState: string
    isCurrentlyRunning: boolean
    currentJobId: string | null
    /// false = removed from Hangfire storage, listed so it can be re-enabled
    enabled: boolean
}

/// One Hangfire queue's live depth (GET /api/schedule/depth)
export interface IQueueDepth {
    queue: string
    enqueued: number
    processing: number
}
