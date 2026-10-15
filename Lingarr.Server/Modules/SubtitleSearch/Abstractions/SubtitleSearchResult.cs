namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Result of one <c>ISubtitleProvider.SearchAsync</c> call: the raw
/// candidates from that single source, score 0 — scoring and cross-source
/// dedup happen in the orchestrator afterwards (③).
/// </summary>
public sealed record SubtitleSearchResult(IReadOnlyList<SubtitleCandidate> Candidates)
{
    public static SubtitleSearchResult Empty { get; } = new([]);
}
