using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Orchestration;

/// <summary>
/// One active per-source cooldown (tech design ⑧), persisted as JSON under
/// <see cref="SettingKeys.SubtitleSearch.SourceCooldowns"/>. The map holds
/// only *active* entries — reading drops expired ones so the job round and
/// the source cards never see stale cooldowns.
/// </summary>
/// <param name="Until">Earliest moment the source may be queried again.</param>
/// <param name="Reason">Bucketed failure reason name (drives the source-card label).</param>
/// <param name="Error">Human-readable failure message.</param>
public sealed record SubtitleSourceCooldown(DateTimeOffset Until, string Reason, string? Error);

/// <summary>
/// Persisted cooldown state of the subtitle sources (⑧): 429→1h (or the
/// longer Retry-After), daily-limit→end of day, 5xx/network→15min,
/// timeout→10min, 401/403→until credentials change
/// (<see cref="CredentialChangeHorizon"/>). Written by the orchestrator
/// whenever a source call fails with a cooldown-worthy reason; read at round
/// start to skip cooled-down sources (a fully cooled-down source set ends
/// the round with a warning, never a retry storm).
/// <para>
/// Scoped by design: <see cref="ISettingService"/> is scoped and the store
/// round-trips through it on every mutation, so every writer sees the latest
/// persisted map (SettingUpdatesHub hot reload applies to reads the same
/// way). No in-memory copy is kept beyond a single method call.
/// </para>
/// </summary>
public sealed class SubtitleSourceCooldownStore
{
    /// <summary>
    /// "Until credentials change" horizon for 401/403 (⑧): a far-future
    /// sentinel rather than null, so the map stays homogeneous and the
    /// source card can render "until credentials change" from the reason.
    /// </summary>
    public static readonly DateTimeOffset CredentialChangeHorizon =
        new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        // Round-trip dates in ISO-8601 with offset — DateTimeOffset default.
        WriteIndented = false
    };

    private readonly ISettingService _settingService;
    private readonly ILogger<SubtitleSourceCooldownStore> _logger;

    public SubtitleSourceCooldownStore(
        ISettingService settingService,
        ILogger<SubtitleSourceCooldownStore> logger)
    {
        _settingService = settingService;
        _logger = logger;
    }

    /// <summary>
    /// The active cooldown map (expired entries dropped). A missing,
    /// blank or unparsable settings row means "no cooldowns" — never an
    /// exception, a corrupt blob must not take the round down.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SubtitleSourceCooldown>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var map = await ReadMapAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var active = new Dictionary<string, SubtitleSourceCooldown>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, cooldown) in map)
        {
            if (cooldown.Until > now)
            {
                active[provider] = cooldown;
            }
        }

        return active;
    }

    /// <summary>
    /// Persists a cooldown for a failed source call. Entries without a
    /// suggested cooldown (MissingCredentials, InvalidResponse, NotFound —
    /// ⑧'s "no cooldown" buckets) are ignored; 401/403 maps onto the
    /// credential-change horizon.
    /// </summary>
    public Task RecordFailureAsync(
        string provider,
        SubtitleProviderException exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var until = exception.Reason switch
        {
            SubtitleProviderFailureReason.Unauthorized => CredentialChangeHorizon,
            _ when exception.Cooldown is { } span => DateTimeOffset.UtcNow + span,
            _ => (DateTimeOffset?)null
        };
        if (until is null)
        {
            return Task.CompletedTask;
        }

        return PersistAsync(provider, until.Value, exception.Reason.ToString(), exception.Message, cancellationToken);
    }

    /// <summary>
    /// Drops one source's cooldown (manual clear, or a successful call
    /// proving the source recovered). Unknown keys are a no-op.
    /// </summary>
    public Task ClearAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return PersistRemovalAsync(provider, cancellationToken);
    }

    private async Task<Dictionary<string, SubtitleSourceCooldown>> ReadMapAsync(
        CancellationToken cancellationToken)
    {
        var json = await _settingService.GetSetting(SettingKeys.SubtitleSearch.SourceCooldowns);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SubtitleSourceCooldown>>(json)
                   ?? [];
        }
        catch (JsonException ex)
        {
            // Corrupt blob: treat as "no cooldowns" and say so — the next
            // RecordFailure overwrites the blob wholesale.
            _logger.LogWarning(ex, "Subtitle source cooldown map is unreadable; ignoring it.");
            return [];
        }
    }

    private async Task PersistAsync(
        string provider,
        DateTimeOffset until,
        string reason,
        string? error,
        CancellationToken cancellationToken)
    {
        var map = await ReadMapAsync(cancellationToken);
        map[provider] = new SubtitleSourceCooldown(until, reason, error);
        await WriteMapAsync(map, cancellationToken);
        _logger.LogInformation(
            "Subtitle source {Provider} enters cooldown until {Until} ({Reason}).",
            provider, until, reason);
    }

    private async Task PersistRemovalAsync(string provider, CancellationToken cancellationToken)
    {
        var map = await ReadMapAsync(cancellationToken);
        if (map.Remove(provider))
        {
            await WriteMapAsync(map, cancellationToken);
        }
    }

    private async Task WriteMapAsync(
        Dictionary<string, SubtitleSourceCooldown> map,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(map, Json);
        await _settingService.SetSetting(SettingKeys.SubtitleSearch.SourceCooldowns, json);
    }
}
