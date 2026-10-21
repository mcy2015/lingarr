namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <summary>
/// One post-processing stage applied to a subtitle file after it lands on
/// disk (B01 framework; B05/B10 mount further stages). Stages are registered
/// in DI and executed by <see cref="CompositePostProcessor"/> in registration
/// order. A stage must never assume it is the only one running: it sees the
/// file as the previous stage left it.
/// </summary>
public interface ISubtitlePostProcessor
{
    /// <summary>
    /// Stable stage identifier used in history-row error annotations
    /// ("<c>{Name}: {message}</c>") and logs. Keep it short and unique.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Processes one landed subtitle file in place. Returns whether the file
    /// content was (or, under <see cref="PostProcessContext.PreviewOnly"/>,
    /// would be) changed. Unapplicable files (wrong mode, wrong format,
    /// nothing to do) are reported as unchanged with a reason — not as
    /// errors. I/O problems may throw; the composite contains them.
    /// </summary>
    /// <param name="filePath">Absolute path of the landed subtitle file.</param>
    /// <param name="context">Stage inputs — currently the Chinese variant mode and the preview flag.</param>
    /// <param name="cancellationToken">Caller cancellation; stages must honor it.</param>
    Task<PostProcessResult> ProcessAsync(
        string filePath,
        PostProcessContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Inputs the pipeline/batch pass to every stage. Extensible for B05/B10;
/// stages ignore members they do not consume.
/// </summary>
/// <param name="ChineseVariantMode">
/// Chinese variant conversion mode ("none"/"simplified"/"traditional" — the
/// import-option vocabulary). Null behaves as "none".
/// </param>
/// <param name="PreviewOnly">
/// True when the caller only wants to know whether the file WOULD change —
/// stages must not write anything in that mode.
/// </param>
public sealed record PostProcessContext(
    string? ChineseVariantMode = null,
    bool PreviewOnly = false);

/// <summary>
/// Outcome of one stage over one file.
/// </summary>
/// <param name="Changed">
/// True when the file content was modified (or would be, under preview).
/// False with a <paramref name="Reason"/> is a normal skip, never an error.
/// </param>
/// <param name="Reason">Human-readable why-not explanation when nothing changed.</param>
/// <param name="Error">
/// Non-throwing failure report; the composite prefixes it with the stage name
/// so it can ride the download history row.
/// </param>
public sealed record PostProcessResult(
    bool Changed,
    string? Reason = null,
    string? Error = null)
{
    /// <summary>Convenience factory for the (very common) no-op case.</summary>
    public static PostProcessResult Unchanged(string? reason = null) => new(false, reason);
}
