using System.Collections.Concurrent;

namespace Lingarr.Server.Modules.SubtitleSearch.Orchestration;

/// <summary>
/// Latest quota snapshot of one subtitle source (R3): what the source's own
/// response headers reported about its daily download quota at the moment of
/// the last observed request. All fields are the source's claims, parsed
/// tolerantly — absent/unparsable headers stay null, never a guess.
/// </summary>
/// <param name="Remaining">Downloads left in the current quota window, when reported.</param>
/// <param name="Total">Published size of the quota window, when the source states one (OpenSubtitles sends no such header — null).</param>
/// <param name="Requests">Quota counter state the source reports alongside the remainder, when present.</param>
/// <param name="ResetUtc">UTC moment the quota window refills (<c>reset_time_utc</c>), when reported.</param>
/// <param name="CapturedAtUtc">When Lingarr observed this snapshot (clock of the capture, not of the source).</param>
public sealed record SubtitleQuotaSnapshot(
    int? Remaining,
    int? Total,
    int? Requests,
    DateTimeOffset? ResetUtc,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Process-wide, in-memory observability of the subtitle sources' quotas
/// (R3). Providers are transient (typed clients resolving scoped settings
/// per call), so a quota fact captured on one response inside one scope is
/// invisible to the next instance — this singleton is the one place the
/// latest snapshot of each source lives. Written by
/// <c>OpenSubtitlesProvider.DownloadAsync</c> (the only source whose quota
/// headers are verified, R3 decision); read by the providers endpoint to
/// aggregate <c>QuotaRemaining</c>/<c>QuotaResetUtc</c> onto the source
/// cards. Other sources may adopt the same store once their header contract
/// is verified (SubDL deliberately not implemented — unverified).
/// <para>
/// In-memory by design (R3 decision): a restart loses the snapshot and the
/// first successful download re-captures it; no persistence, no migration.
/// Thread-safe: a last-writer-wins map keyed on the source key — concurrent
/// downloads of one source only ever replace "latest" with "slightly newer
/// latest", which is the observable contract.
/// </para>
/// </summary>
public sealed class SubtitleQuotaStore
{
    private readonly ConcurrentDictionary<string, SubtitleQuotaSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stores the latest snapshot of one source (replaces any previous).</summary>
    public void Record(string provider, SubtitleQuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[provider] = snapshot;
    }

    /// <summary>The latest snapshot of one source; null when nothing was captured yet.</summary>
    public SubtitleQuotaSnapshot? Find(string provider)
        => _snapshots.TryGetValue(provider, out var snapshot) ? snapshot : null;
}
