namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <summary>
/// Runs every registered <see cref="ISubtitlePostProcessor"/> over one landed
/// subtitle file, in DI registration order (B01). Failure containment is the
/// contract: a stage that throws is logged as a warning and the run continues
/// with the next stage — post-processing must never block a file that is
/// already on disk. Collected failures surface as <see cref="PostProcessResult.Error"/>
/// with the stage name prefix ("<c>{Name}: {message}</c>") so the download
/// history row can carry them. Caller cancellation is the only propagation.
/// </summary>
public sealed class CompositePostProcessor
{
    private readonly IEnumerable<ISubtitlePostProcessor> _stages;
    private readonly ILogger<CompositePostProcessor> _logger;

    public CompositePostProcessor(
        IEnumerable<ISubtitlePostProcessor> stages,
        ILogger<CompositePostProcessor> logger)
    {
        // Kept as the enumerable and re-read per call: DI hands over a
        // snapshot, while tests mutate the stage list before invoking.
        _stages = stages;
        _logger = logger;
    }

    /// <summary>
    /// Processes one file through all stages in registration order. Never
    /// throws except for caller cancellation — per-stage exceptions become
    /// warnings and error annotations.
    /// </summary>
    public async Task<PostProcessResult> ProcessAsync(
        string filePath,
        PostProcessContext context,
        CancellationToken cancellationToken)
    {
        var changed = false;
        List<string>? errors = null;

        foreach (var stage in _stages)
        {
            try
            {
                var result = await stage.ProcessAsync(filePath, context, cancellationToken);
                changed |= result.Changed;
                if (result.Error is not null)
                {
                    (errors ??= []).Add($"{stage.Name}: {result.Error}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-processing stage {Stage} failed for {FilePath}; the subtitle stays on disk as written.",
                    stage.Name, filePath);
                (errors ??= []).Add($"{stage.Name}: {ex.Message}");
            }
        }

        return new PostProcessResult(
            changed,
            Error: errors is null ? null : string.Join("; ", errors));
    }
}
