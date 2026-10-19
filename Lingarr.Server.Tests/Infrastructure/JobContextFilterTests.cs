using System;
using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Lingarr.Server.Filters;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// NAS incident 2026-10-19: a Hangfire SQLite "database is locked" storm
/// failed job creation, and the failure-path CreatedContext carries a null
/// Job — the filter's unconditional dereference replaced the real storage
/// exception with an NRE, showing every enqueue endpoint (webhook, sync
/// triggers, translation queue) a vague 500. These tests pin the guards:
/// failure-shaped contexts pass through silently instead of throwing.
/// The contexts are built uninitialized on purpose — their constructors
/// demand live storage connections the guard has no business touching.
/// </summary>
public class JobContextFilterTests
{
    private static T Uninitialized<T>() where T : class =>
        (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    [Fact]
    public void OnCreated_FailureContextWithNullJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnCreated(Uninitialized<CreatedContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
    }

    [Fact]
    public void OnCreating_NullJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnCreating(Uninitialized<CreatingContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
    }

    [Fact]
    public void OnStateElection_NullBackgroundJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnStateElection(Uninitialized<ElectStateContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
    }

    [Fact]
    public void OnPerformed_NullBackgroundJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnPerformed(Uninitialized<PerformedContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
    }
}
