using System.Collections.Concurrent;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services.Plugins;
using Lingarr.Contracts.Plugins;

namespace Lingarr.Server.Services;

/// <summary>
/// In-memory health tracker: a bounded ring of recent request outcomes per
/// provider, fed from <see cref="Translation.Base.BaseTranslationService"/>'s
/// shared retry loop. Process-local by design — health is a "since restart"
/// signal; durable per-request usage stats belong to the cost work (A5).
/// </summary>
public class TranslationHealthService : ITranslationHealthService
{
    private const int WindowSize = 20;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<HealthSample>> _samples = new();
    private readonly IPluginRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The tracker is a singleton but reads settings (scoped) for the
    /// configuration check, so it opens its own scope per aggregation call
    /// instead of capturing a scoped service.
    /// </summary>
    public TranslationHealthService(IPluginRegistry registry, IServiceScopeFactory scopeFactory)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
    }

    public void Record(string serviceType, bool success, TimeSpan latency, string? error = null)
    {
        var queue = _samples.GetOrAdd(serviceType, _ => new ConcurrentQueue<HealthSample>());
        queue.Enqueue(new HealthSample(DateTimeOffset.UtcNow, success, latency.TotalMilliseconds, error));
        while (queue.Count > WindowSize && queue.TryDequeue(out _))
        {
            // trim to the rolling window
        }
    }

    public TranslationHealthSnapshot? GetSnapshot(string serviceType)
    {
        if (!_samples.TryGetValue(serviceType, out var queue) || queue.IsEmpty)
        {
            return null;
        }

        var samples = queue.ToArray();
        var successLatencies = samples.Where(s => s.Success).Select(s => s.LatencyMs).ToArray();
        var last = samples[^1];
        return new TranslationHealthSnapshot
        {
            ServiceType = serviceType,
            AverageLatencyMs = successLatencies.Length > 0 ? successLatencies.Average() : null,
            SuccessRate = samples.Count(s => s.Success) / (double)samples.Length,
            LastError = samples
                .Where(s => s.Error is not null)
                .OrderByDescending(s => s.At)
                .Select(s => s.Error)
                .FirstOrDefault(),
            LastRequestAt = last.At,
            SampleCount = samples.Length
        };
    }

    public async Task<List<TranslationServiceHealth>> GetServiceHealthAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var health = new List<TranslationServiceHealth>();
        foreach (var plugin in _registry.All)
        {
            var missingFields = new List<string>();
            foreach (var field in plugin.Manifest.Settings)
            {
                if (!field.Required)
                {
                    continue;
                }

                var value = field.Type == PluginSettingType.Secret
                    ? await settings.GetEncryptedSetting(field.Key)
                    : await settings.GetSetting(field.Key);
                if (string.IsNullOrEmpty(value))
                {
                    missingFields.Add(field.Key);
                }
            }

            var snapshot = GetSnapshot(plugin.Manifest.Provider);
            health.Add(new TranslationServiceHealth
            {
                Provider = plugin.Manifest.Provider,
                DisplayName = plugin.Manifest.DisplayName,
                Configured = missingFields.Count == 0,
                LatencyMs = snapshot?.AverageLatencyMs,
                SuccessRate = snapshot?.SuccessRate,
                LastError = snapshot?.LastError,
                LastRequestAt = snapshot?.LastRequestAt,
                SampleCount = snapshot?.SampleCount ?? 0
            });
        }

        return health;
    }

    public async Task<int> GetOnlineServiceCountAsync()
    {
        var health = await GetServiceHealthAsync();
        // Without recent traffic, configured is the best proxy for "online";
        // a failing window demotes a configured provider until it recovers.
        return health.Count(h => h.Configured && (h.SuccessRate is null || h.SuccessRate >= 0.5));
    }

    private readonly record struct HealthSample(DateTimeOffset At, bool Success, double LatencyMs, string? Error);
}
