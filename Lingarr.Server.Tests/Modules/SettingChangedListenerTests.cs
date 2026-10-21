using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules;

/// <summary>
/// P1 regression cover: the settings-change listener must actually be
/// subscribed for setting writes to reach it (the DI registration alone
/// never constructed the singleton), and the automation/schedule reaction
/// groups stay pruned — recurring-job lifecycle belongs to ScheduleService.
/// </summary>
/// <summary>
/// Both classes that swap the process-global <c>JobStorage.Current</c>
/// (this one and <c>ScheduleServiceTests</c>) must never run in parallel
/// with each other: a concurrent swap makes the other class's
/// <c>GetRecurringJobs</c> read a storage it did not install.
/// </summary>
[CollectionDefinition("HangfireGlobalStorage", DisableParallelization = true)]
public class HangfireGlobalStorageCollection
{
}

[Collection("HangfireGlobalStorage")]
public class SettingChangedListenerTests : IDisposable
{
    private readonly SettingChangeNotifier _notifier = new();
    private readonly JobStorage? _previousStorage;

    public SettingChangedListenerTests()
    {
        _previousStorage = TryGetCurrentStorage();
        JobStorage.Current = new MemoryStorage();
    }

    private static JobStorage? TryGetCurrentStorage()
    {
        try
        {
            return JobStorage.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        JobStorage.Current = _previousStorage!;
        GC.SuppressFinalize(this);
    }

    private ServiceProvider BuildProvider()
    {
        // Mirrors SettingsModule: the listener is registered as a singleton
        // that nothing injects — only an explicit resolution constructs it.
        var services = new ServiceCollection();
        services.AddSingleton<ISettingChangeNotifier>(_notifier);
        services.AddSingleton<SettingChangedListener>();
        services.AddSingleton(new Mock<IHubContext<SettingUpdatesHub>>().Object);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ResolvedFromProvider_SubscribesToNotifier()
    {
        using var provider = BuildProvider();

        var listener = provider.GetRequiredService<SettingChangedListener>();

        // The subscription lives in the constructor; a resolved instance
        // must therefore appear in the notifier's invocation list.
        var backing = typeof(SettingChangeNotifier)
            .GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(backing);
        var handlers = (Delegate?)backing.GetValue(_notifier);
        Assert.NotNull(handlers);
        Assert.Contains(handlers!.GetInvocationList(),
            handler => handler.Target == listener && handler.Method.Name == "OnSettingChanged");
    }

    [Fact]
    public async Task SettingChange_ForPrunedGroups_DoesNotTouchRecurringJobs()
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<SettingChangedListener>();

        // automation/schedule setting keys no longer drive Hangfire from the
        // listener — the fire-and-forget handler must leave storage alone.
        _notifier.Notify(null!, "automation_enabled");
        _notifier.Notify(null!, "translation_schedule");
        _notifier.Notify(null!, "movie_schedule");
        _notifier.Notify(null!, "show_schedule");
        await Task.Delay(100);

        Assert.Empty(JobStorage.Current.GetConnection().GetRecurringJobs());
    }
}
