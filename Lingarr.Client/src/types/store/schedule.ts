export interface IUseScheduleStore {
    recurringJobs: IRecurringJob[]
    queueDepths: IQueueDepth[]
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

/// Body of POST /api/schedule/job — new recurring job creation
export interface ICreateRecurringJobRequest {
    jobId: string
    jobType: string
    cron: string
}

/// Response of POST /api/schedule/job — the freshly registered job
export interface IRecurringJobStatus {
    id: string
    cron: string
    jobMethod: string
    enabled: boolean
}
