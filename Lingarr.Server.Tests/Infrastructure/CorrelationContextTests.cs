using System;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Infrastructure;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// L2 correlation context semantics: the five keys round-trip through the
/// ambient state, Push/Dispose restores nested snapshots, blank values
/// normalize to "no stamp", and — the property the parallel orchestrator
/// rounds depend on — a write performed inside a forked flow never leaks out
/// to the parent or sibling flows, while keys set before an await stay
/// visible to awaited callees (what both log sinks rely on when stamping).
/// Every test resets the context in a finally: xUnit may run later cases of
/// other classes on this thread, and a stale ambient key would misattribute
/// their assertions.
/// </summary>
public class CorrelationContextTests
{
    private static CorrelationContext.Snapshot Capture() => CorrelationContext.Capture();

    [Fact]
    public void Setters_RoundTripAllFiveKeys()
    {
        try
        {
            CorrelationContext.SetJobId("hangfire-17");
            CorrelationContext.SetRequestId("42");
            CorrelationContext.SetSearchSessionId(7);
            CorrelationContext.SetMediaKey("Movie/123");
            CorrelationContext.SetProvider("assrt");

            Assert.Equal("hangfire-17", CorrelationContext.JobId);
            Assert.Equal("42", CorrelationContext.RequestId);
            Assert.Equal(7, CorrelationContext.SearchSessionId);
            Assert.Equal("Movie/123", CorrelationContext.MediaKey);
            Assert.Equal("assrt", CorrelationContext.Provider);

            var snapshot = Capture();
            Assert.Equal("hangfire-17", snapshot.JobId);
            Assert.Equal("42", snapshot.RequestId);
            Assert.Equal(7, snapshot.SearchSessionId);
            Assert.Equal("Movie/123", snapshot.MediaKey);
            Assert.Equal("assrt", snapshot.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Setters_BlankValuesNormalizeToNull()
    {
        try
        {
            CorrelationContext.SetJobId("real");
            CorrelationContext.SetJobId(string.Empty);
            CorrelationContext.SetProvider("   ");

            Assert.Null(CorrelationContext.JobId);
            Assert.Null(CorrelationContext.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Clear_ResetsEveryKey()
    {
        try
        {
            CorrelationContext.SetJobId("j");
            CorrelationContext.SetRequestId("r");
            CorrelationContext.SetSearchSessionId(1);
            CorrelationContext.SetMediaKey("m");
            CorrelationContext.SetProvider("p");

            CorrelationContext.Clear();

            Assert.Null(CorrelationContext.JobId);
            Assert.Null(CorrelationContext.RequestId);
            Assert.Null(CorrelationContext.SearchSessionId);
            Assert.Null(CorrelationContext.MediaKey);
            Assert.Null(CorrelationContext.Provider);
            Assert.Equal(default, Capture());
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Push_DisposeRestoresTheExactPreviousSnapshot()
    {
        try
        {
            CorrelationContext.SetJobId("outer-job");
            CorrelationContext.SetProvider("outer-source");

            using (CorrelationContext.Push(requestId: "9", provider: "inner-source"))
            {
                // Provided keys applied, unprovided keys untouched.
                Assert.Equal("outer-job", CorrelationContext.JobId);
                Assert.Equal("9", CorrelationContext.RequestId);
                Assert.Equal("inner-source", CorrelationContext.Provider);
            }

            // Disposal restores the pre-push state — including the provider
            // the inner push overrode.
            Assert.Equal("outer-job", CorrelationContext.JobId);
            Assert.Null(CorrelationContext.RequestId);
            Assert.Equal("outer-source", CorrelationContext.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Push_NestingUnwindsInReverseOrder()
    {
        try
        {
            using var outer = CorrelationContext.Push(searchSessionId: 7);
            using (var inner = CorrelationContext.Push(provider: "opensubtitles"))
            {
                Assert.Equal(7, CorrelationContext.SearchSessionId);
                Assert.Equal("opensubtitles", CorrelationContext.Provider);
            }

            Assert.Equal(7, CorrelationContext.SearchSessionId);
            Assert.Null(CorrelationContext.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Push_SecondDisposeDoesNotWipeTheAmbientState()
    {
        try
        {
            CorrelationContext.SetJobId("survivor");

            var scope = CorrelationContext.Push(provider: "once");
            scope.Dispose();
            scope.Dispose(); // defensive double dispose

            Assert.Equal("survivor", CorrelationContext.JobId);
            Assert.Null(CorrelationContext.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public async Task MutationInsideAForkedFlow_StaysInsideTheFork()
    {
        try
        {
            CorrelationContext.SetProvider("round-source");

            // Task.Run inherits the ambient snapshot at fork time; writes the
            // fork performs must not reach the joining parent (copy-on-write)
            // — the property that keeps the orchestrator's parallel
            // per-source iterations from stamping each other's logs.
            await Task.Run(() => CorrelationContext.SetProvider("fork-source"));

            Assert.Equal("round-source", CorrelationContext.Provider);

            // The parent's own writes likewise never reach an already-joined
            // sibling flow: a second fork sees the parent value it captured,
            // not whatever a first fork wrote.
            var seen = await Task.Run(() => CorrelationContext.Provider);
            Assert.Equal("round-source", seen);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public async Task KeySetInAnAsyncMethod_SurvivesAwaitsInsideIt()
    {
        // The pattern every injection point uses: set at entry (or after an
        // await), then keep logging across further awaits. The ambient value
        // must remain readable for the whole logical flow and reach awaited
        // callees — that is how the sinks stamp entries written deep inside
        // provider/pipeline code.
        try
        {
            await StampAndProbeAsync("transient-source", "transient-media");
            Assert.Null(CorrelationContext.Provider);
            Assert.Null(CorrelationContext.MediaKey);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    private static async Task StampAndProbeAsync(string provider, string mediaKey)
    {
        using var scope = CorrelationContext.Push(provider: provider, mediaKey: mediaKey);
        await Task.Yield();

        Assert.Equal(provider, CorrelationContext.Provider);
        Assert.Equal(mediaKey, CorrelationContext.MediaKey);

        var probed = await Task.Run(() => CorrelationContext.Capture());
        Assert.Equal(provider, probed.Provider);
        Assert.Equal(mediaKey, probed.MediaKey);
        // The fork saw the values but cannot have cleared them for us.
        Assert.Equal(provider, CorrelationContext.Provider);
    }
}
