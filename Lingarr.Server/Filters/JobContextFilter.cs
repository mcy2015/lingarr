using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Filters;

public class JobContextFilter : IClientFilter, IServerFilter, IElectStateFilter, IServerExceptionFilter
{
    private static readonly AsyncLocal<string> JobTypeName = new();

    private readonly ILogger<JobContextFilter>? _logger;

    /// <summary>
    /// App service provider for the job_failed notification hook (B11).
    /// Null in tests and any host that wires the filter without DI — the
    /// hook is best-effort and silently absent then. The filter never
    /// resolves scoped services from the root: each dispatch opens its own
    /// scope inside the fire-and-forget task.
    /// </summary>
    private readonly IServiceProvider? _serviceProvider;

    public JobContextFilter(ILogger<JobContextFilter>? logger = null, IServiceProvider? serviceProvider = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
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
        NotifyJobFailed(jobType, context.Exception);
    }

    /// <summary>
    /// job_failed notification hook (B11): fire-and-forget so the sync
    /// safety net above stays synchronous. The whole chain — Task.Run, the
    /// scope, the dispatch — is wrapped so a notification problem can never
    /// disturb the failure Hangfire is about to record. The event carries
    /// the job type as its subject (a job failure has no media row to
    /// resolve a title from).
    /// </summary>
    private void NotifyJobFailed(string jobType, Exception exception)
    {
        if (_serviceProvider is null)
        {
            return;
        }

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var notifications = scope.ServiceProvider.GetService<NotificationService>();
                    if (notifications is null)
                    {
                        return;
                    }

                    await notifications.NotifyAsync(new NotificationEvent(
                        NotificationService.EventJobFailed,
                        Title: jobType,
                        Result: "failed",
                        Detail: exception.Message), CancellationToken.None);
                }
                catch
                {
                    // best-effort by contract — the log entry above is the
                    // failure's permanent trace
                }
            });
        }
        catch
        {
            // Task.Run itself can throw during shutdown — same contract.
        }
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