using System;
using System.Collections.Generic;
using System.Reflection;
using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Lingarr.Server.Filters;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Logging;
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
/// <para>
/// L2 additions: since the job id moved into CorrelationContext (the single
/// source both log sinks stamp from), OnPerforming must publish it there and
/// OnPerformed must clear it — the correlation hygiene that stops post-job
/// log lines of a worker flow from being attributed to the finished job.
/// </para>
/// </summary>
public class JobContextFilterTests
{
    private static T Uninitialized<T>() where T : class =>
        (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    // A stand-in method payload for the reflection-built Hangfire Job DTO —
    // OnPerforming only reads Job.Type.Name, which this satisfies. Public:
    // Hangfire's Job.Validate rejects non-public methods.
    public static void SampleJobMethod()
    {
    }

    /// <summary>
    /// Builds a performing-shaped context around a real BackgroundJob DTO.
    /// Reflection-based so the test neither names Hangfire's internal DTO
    /// namespaces nor depends on their constructor signatures beyond
    /// "a (Type, MethodInfo, object[]) job ctor" and "a (string, job,
    /// DateTime) background-job ctor" — the shapes every 1.8.x ships.
    /// </summary>
    private static T PerformingShaped<T>(string jobId) where T : class
    {
        var context = Uninitialized<T>();
        var property = typeof(T).GetProperty("BackgroundJob")
            ?? typeof(PerformContext).GetProperty("BackgroundJob")
            ?? throw new InvalidOperationException("PerformContext.BackgroundJob not found.");

        var backgroundJob = BuildBackgroundJob(property.PropertyType, jobId);
        if (property.CanWrite)
        {
            property.SetValue(context, backgroundJob);
        }
        else
        {
            var field = (property.DeclaringType ?? typeof(PerformContext))
                .GetField("<BackgroundJob>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BackgroundJob backing field not found.");
            field.SetValue(context, backgroundJob);
        }

        return context;
    }

    private static object BuildBackgroundJob(Type backgroundJobType, string jobId)
    {
        var jobProperty = backgroundJobType.GetProperty("Job")
            ?? throw new InvalidOperationException("BackgroundJob.Job not found.");
        var job = BuildJob(jobProperty.PropertyType);

        foreach (var constructor in backgroundJobType.GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 3
                && parameters[0].ParameterType == typeof(string)
                && parameters[2].ParameterType == typeof(DateTime))
            {
                return constructor.Invoke(new[] { jobId, job, DateTime.UtcNow });
            }
        }

        throw new InvalidOperationException("No (string, Job, DateTime) constructor on BackgroundJob.");
    }

    private static object BuildJob(Type jobType)
    {
        var method = typeof(JobContextFilterTests).GetMethod(
            nameof(SampleJobMethod), BindingFlags.Public | BindingFlags.Static);
        foreach (var constructor in jobType.GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 3
                && parameters[0].ParameterType == typeof(Type)
                && parameters[1].ParameterType == typeof(MethodInfo))
            {
                return constructor.Invoke(new object[] { typeof(JobContextFilterTests), method!, Array.Empty<object>() });
            }
        }

        throw new InvalidOperationException("No (Type, MethodInfo, object[]) constructor on Job.");
    }

    [Fact]
    public void OnCreated_FailureContextWithNullJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnCreated(Uninitialized<CreatedContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
    }

    [Fact]
    public void OnCreating_NullJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnCreating(Uninitialized<CreatingContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
    }

    [Fact]
    public void OnStateElection_NullBackgroundJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnStateElection(Uninitialized<ElectStateContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
    }

    [Fact]
    public void OnPerformed_NullBackgroundJob_DoesNotThrow()
    {
        var filter = new JobContextFilter();

        filter.OnPerformed(Uninitialized<PerformedContext>());

        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
        Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobTypeName());
    }

    // ── L2 correlation ──────────────────────────────────────────────────

    [Fact]
    public void OnPerforming_PublishesJobIdIntoCorrelationContext()
    {
        try
        {
            var filter = new JobContextFilter();

            filter.OnPerforming(PerformingShaped<PerformingContext>("job-42"));

            // The ambient key both log sinks stamp from, and the legacy
            // getter surface kept byte-identical.
            Assert.Equal("job-42", CorrelationContext.JobId);
            Assert.Equal("job-42", JobContextFilter.GetCurrentJobId());
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void OnPerformed_AfterPerforming_ClearsCorrelationJobId()
    {
        try
        {
            var filter = new JobContextFilter();
            filter.OnPerforming(PerformingShaped<PerformingContext>("job-42"));

            filter.OnPerformed(PerformingShaped<PerformedContext>("job-42"));

            // The job body has finished — later ambient log lines of this
            // flow must not be attributed to its id anymore.
            Assert.Null(CorrelationContext.JobId);
            Assert.Equal(string.Empty, JobContextFilter.GetCurrentJobId());
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    // ── L3 server exception safety net ────────────────────────────────

    [Fact]
    public void OnServerException_LogsErrorWithJobIdTypeAndFullException()
    {
        var logger = new CapturingLogger();
        var filter = new JobContextFilter(logger);
        // ServerExceptionContext's copy constructor demands a live storage
        // connection on the source context, so the DTO is built
        // uninitialized with the two properties injected through their
        // setters/backing fields — same discipline as PerformingShaped.
        var context = Uninitialized<ServerExceptionContext>();
        var thrown = new InvalidOperationException("boom");
        Inject(context, typeof(ServerExceptionContext).GetProperty("BackgroundJob")
            ?? typeof(PerformContext).GetProperty("BackgroundJob")
            ?? throw new InvalidOperationException("BackgroundJob property not found."),
            BuildBackgroundJob(typeof(PerformContext).GetProperty("BackgroundJob")!.PropertyType, "job-77"));
        Inject(context, typeof(ServerExceptionContext).GetProperty("Exception")
            ?? throw new InvalidOperationException("Exception property not found."),
            thrown);

        try
        {
            filter.OnServerException(context);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("job-77", entry.Message);
            Assert.Contains(nameof(JobContextFilterTests), entry.Message);
            Assert.Same(thrown, entry.Exception);
            // The log call happened inside the correlation scope — the
            // sinks would stamp this job id onto the entry.
            Assert.Equal("job-77", entry.JobIdAtLogTime);
            // Scope disposed on return: ambient attribution ends here.
            Assert.Null(CorrelationContext.JobId);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    /// <summary>
    /// Sets a possibly read-only property via its setter or backing field.
    /// </summary>
    private static void Inject(object target, PropertyInfo property, object value)
    {
        if (property.CanWrite)
        {
            property.SetValue(target, value);
            return;
        }

        var field = (property.DeclaringType ?? target.GetType())
            .GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{property.Name} backing field not found.");
        field.SetValue(target, value);
    }

    [Fact]
    public void OnServerException_FailureContextWithNullJob_DoesNotThrow()
    {
        var logger = new CapturingLogger();
        var filter = new JobContextFilter(logger);

        filter.OnServerException(Uninitialized<ServerExceptionContext>());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("unknown", entry.Message);
    }

    private sealed class CapturingLogger : ILogger<JobContextFilter>
    {
        public sealed record Entry(
            LogLevel Level,
            string Message,
            Exception? Exception,
            string? JobIdAtLogTime);

        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new Entry(logLevel, formatter(state, exception), exception, CorrelationContext.JobId));
    }
}
