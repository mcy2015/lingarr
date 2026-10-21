using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Filters;

public class JobContextFilter : IClientFilter, IServerFilter, IElectStateFilter, IServerExceptionFilter
{
    private static readonly AsyncLocal<string> JobTypeName = new();

    private readonly ILogger<JobContextFilter>? _logger;

    public JobContextFilter(ILogger<JobContextFilter>? logger = null)
    {
        _logger = logger;
    }

    // L2: the job id now lives in CorrelationContext (the single source the
    // log sinks stamp from) instead of a second AsyncLocal here — two copies
    // of the same key would drift apart the first time one path forgot to
    // mirror the other. GetCurrentJobId keeps its pre-L2 contract: empty
    // string when no id is ambient.

    public void OnCreating(CreatingContext context)
    {
        JobTypeName.Value = context.Job?.Type?.Name ?? JobTypeName.Value;
        CorrelationContext.SetJobId(null);
    }

    public void OnCreated(CreatedContext context)
    {
        // Creation-failure contexts reach the filter with a null Job —
        // dereferencing it unconditionally turned every storage failure
        // (the NAS "database is locked" storm, 2026-10-19) into an NRE
        // that masked the real exception behind a vague 500.
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        CorrelationContext.SetJobId(context.BackgroundJob?.Id ?? CorrelationContext.JobId);
    }

    public void OnPerforming(PerformingContext context)
    {
        JobTypeName.Value = context.BackgroundJob.Job.Type.Name;
        CorrelationContext.SetJobId(context.BackgroundJob.Id);
    }

    public void OnPerformed(PerformedContext context)
    {
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        // L2 correlation hygiene: the job body has finished, so ambient log
        // lines of this flow stop being attributed to its id. A later
        // OnStateElection in the same flow deliberately re-establishes the
        // id — those entries describe this job's state transition. The next
        // job's OnPerforming overwrites the id before its body logs anyway.
        CorrelationContext.SetJobId(null);
    }

    public void OnStateElection(ElectStateContext context)
    {
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        CorrelationContext.SetJobId(context.BackgroundJob?.Id ?? CorrelationContext.JobId);
    }

    /// <summary>
    /// Terminal safety net for job bodies that throw: one Error entry with
    /// the full exception, job id and job type before Hangfire records the
    /// failure. Jobs that swallow their own exceptions (the sync jobs log
    /// and stop under AutomaticRetry Attempts=0) never reach this — their
    /// own catch is the only trace, which is why the schedule last_error
    /// columns exist.
    /// </summary>
    public void OnServerException(ServerExceptionContext context)
    {
        var jobType = context.BackgroundJob?.Job?.Type?.Name ?? "unknown";
        JobTypeName.Value = jobType;
        using var correlation = CorrelationContext.Push(jobId: context.BackgroundJob?.Id);
        _logger?.LogError(
            context.Exception,
            "Background job {JobId} ({JobType}) failed.",
            context.BackgroundJob?.Id ?? "unknown",
            jobType);
    }

    public static string GetCurrentJobTypeName()
    {
        return JobTypeName.Value ?? string.Empty;
    }

    public static string GetCurrentJobId()
    {
        return CorrelationContext.JobId ?? string.Empty;
    }
}