using System.ComponentModel.DataAnnotations;
using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

/// <summary>
/// One persisted download attempt in the subtitle-search history (M0008).
/// Rows are appended by the download pipeline (⑦) for every outcome —
/// successes, failures, low-confidence skips and already-exists skips — so
/// the history screen and the KPIs count from real attempts only. A
/// re-download simply lands a new file and a new row: the never-implemented
/// replacement-chain columns were dropped in M0010 (dead schema misleads),
/// and the files themselves stay on disk for the user's cleanup policy.
/// <para>
/// Not to be confused with the ABI record of the same name
/// (Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload),
/// which is the raw fetched payload, not the row.
/// </para>
/// </summary>
public class SubtitleDownload
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owner media type — Movie or Episode (polymorphic, no FK).</summary>
    public required MediaType MediaType { get; set; }

    /// <summary>Id of the owning movie / episode.</summary>
    public required int MediaId { get; set; }

    /// <summary>Source key ("opensubtitles", "assrt"…).</summary>
    public required string Provider { get; set; }

    /// <summary>Source-side id of the subtitle entry.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Normalized language code of the downloaded track.</summary>
    public required string Language { get; set; }

    /// <summary>Where the file landed (naming-template path).</summary>
    public required string FilePath { get; set; }

    /// <summary>Match score at decision time (③: hash hit = 100 short-circuit).</summary>
    public required int MatchScore { get; set; }

    /// <summary>Whether the candidate came from a hash match.</summary>
    public required bool HashMatch { get; set; }

    /// <summary>How the download was initiated.</summary>
    public SubtitleDownloadSource Source { get; set; } = SubtitleDownloadSource.Auto;

    /// <summary>Link to the subtitle_search_sessions row of the deciding search, when known.</summary>
    public int? SearchSessionId { get; set; }

    /// <summary>Outcome of the attempt.</summary>
    public required SubtitleDownloadResult Result { get; set; }

    /// <summary>Failure reason when <see cref="Result"/> is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>Attempt timestamp — the table deliberately has no updated_at.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}

/// <summary>How a download was initiated (M0008 int codes).</summary>
public enum SubtitleDownloadSource
{
    /// <summary>User picked a candidate in the search dialog.</summary>
    Manual = 0,

    /// <summary>Automated batch crossed the auto-score threshold.</summary>
    Auto = 1,

    /// <summary>Webhook-triggered round.</summary>
    Webhook = 2
}

/// <summary>Outcome of a download attempt (M0008 int codes).</summary>
public enum SubtitleDownloadResult
{
    /// <summary>File fetched, processed and written.</summary>
    Succeeded = 0,

    /// <summary>Fetch or pipeline failure (see Error).</summary>
    Failed = 1,

    /// <summary>Best candidate below the auto-score threshold (③).</summary>
    LowConfidenceSkipped = 2,

    /// <summary>A subtitle for the language already existed.</summary>
    AlreadyExistsSkipped = 3
}
