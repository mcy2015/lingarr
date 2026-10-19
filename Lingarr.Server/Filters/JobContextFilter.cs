using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;

namespace Lingarr.Server.Filters;

public class JobContextFilter : IClientFilter, IServerFilter, IElectStateFilter
{
    private static readonly AsyncLocal<string> JobTypeName = new();
    private static readonly AsyncLocal<string> JobId = new();

    public void OnCreating(CreatingContext context)
    {
        JobTypeName.Value = context.Job?.Type?.Name ?? JobTypeName.Value;
        JobId.Value = string.Empty;
    }

    public void OnCreated(CreatedContext context)
    {
        // Creation-failure contexts reach the filter with a null Job —
        // dereferencing it unconditionally turned every storage failure
        // (the NAS "database is locked" storm, 2026-10-19) into an NRE
        // that masked the real exception behind a vague 500.
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        JobId.Value = context.BackgroundJob?.Id ?? JobId.Value;
    }

    public void OnPerforming(PerformingContext context)
    {
        JobTypeName.Value = context.BackgroundJob.Job.Type.Name;
        JobId.Value = context.BackgroundJob.Id;
    }

    public void OnPerformed(PerformedContext context)
    {
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        JobId.Value = context.BackgroundJob?.Id ?? JobId.Value;
    }

    public void OnStateElection(ElectStateContext context)
    {
        JobTypeName.Value = context.BackgroundJob?.Job?.Type?.Name ?? JobTypeName.Value;
        JobId.Value = context.BackgroundJob?.Id ?? JobId.Value;
    }
    
    public static string GetCurrentJobTypeName()
    {
        return JobTypeName.Value ?? string.Empty;
    }

    public static string GetCurrentJobId()
    {
        return JobId.Value ?? string.Empty;
    }
}