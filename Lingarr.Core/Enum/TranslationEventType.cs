namespace Lingarr.Core.Enum;

/// <summary>
/// Fine-grained timeline event kinds recorded on a translation request
/// (PM analysis P1-2, fallback visibility). Null on legacy rows that only
/// carry a status transition — those render by status as before.
/// </summary>
public enum TranslationEventType
{
    /// <summary>Request row created (status Pending).</summary>
    Created,

    /// <summary>Hangfire job enqueued for the request.</summary>
    Enqueued,

    /// <summary>Translation job started processing.</summary>
    Started,

    /// <summary>A chain provider failed for this request (message carries status/detail).</summary>
    ProviderFailed,

    /// <summary>Fallback moved work from one provider to the next one that succeeded.</summary>
    Degraded,

    /// <summary>One batch of a batch-mode translation completed (message carries n/total).</summary>
    BatchCompleted,

    /// <summary>Request reached Completed.</summary>
    Completed,

    /// <summary>Request reached Failed.</summary>
    Failed,

    /// <summary>Translated subtitle written to disk (message carries the output path).</summary>
    Writeback
}
