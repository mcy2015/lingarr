using Lingarr.Contracts.Models;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Persists translation progress and streams it over SignalR (scale audit
/// P0-10, P2 broadcast collapse).
///
/// Broadcast grouping: every message goes only to the per-request group
/// (<c>TranslationRequest-{id}</c>). The global <c>TranslationRequests</c>
/// group no longer carries per-request traffic — it remains in use solely
/// for the low-frequency <c>ActiveTranslations</c> summary that
/// <see cref="TranslationRequestQueryService.UpdateActiveCount"/> pushes on
/// job lifecycle transitions (the topbar badge feed). Queue/dashboard screens
/// join the per-request groups of the rows they actually show; the detail
/// screen joins exactly its own request's group.
///
/// Progress throttling: mid-run <see cref="Emit"/> calls are collapsed to at
/// most one message per ≥2% progress delta (or 500ms, whichever comes first);
/// the first emission, every status change and the terminal state always go
/// out. The persisted <c>Progress</c> column follows the same rhythm, which
/// also removes most of the per-percent <c>SaveChanges</c> write amplification.
///
/// Line batching: <see cref="EmitLine"/> results are buffered per request and
/// written in batches of <see cref="LineFlushBatchSize"/> rows; the broadcasts
/// ride the same rhythm — pending line payloads are flushed as a single
/// <c>LineTranslatedBatch</c> message per <see cref="LineBroadcastBatchSize"/>
/// lines or <see cref="LineBroadcastWindow"/> elapsed time (whichever comes
/// first; the window is checked lazily on the next buffered line, so no
/// timers run). Any terminal <see cref="Emit"/> (completed/failed/cancelled/
/// paused) flushes both buffers — the buffered line batch precedes the
/// terminal progress message so a live detail view appends the tail before
/// the completion status lands — and a finished request always has its full
/// line set on disk. If the process dies mid-run, the flushed batches persist
/// and the unflushed tail is simply re-translated on resume — the job's
/// checkpoint reads persisted rows by position, so already-batched lines are
/// never duplicated.
/// </summary>
public class ProgressService : IProgressService
{
    private const string RequestGroupPrefix = "TranslationRequest-";
    private const int LineFlushBatchSize = 50;
    private static readonly TimeSpan ProgressMinInterval = TimeSpan.FromMilliseconds(500);
    private const int ProgressMinDelta = 2;
    /// <summary>
    /// Line broadcasts leave this service only as <c>LineTranslatedBatch</c>
    /// array messages, flushed after this many buffered lines or after
    /// <see cref="LineBroadcastWindow"/> elapsed (whichever comes first).
    /// </summary>
    private const int LineBroadcastBatchSize = 50;
    private static readonly TimeSpan LineBroadcastWindow = TimeSpan.FromMilliseconds(50);

    private readonly IHubContext<TranslationRequestsHub> _hubContext;
    private readonly LingarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<int, RequestProgressState> _states = [];

    public ProgressService(
        IHubContext<TranslationRequestsHub> hubContext,
        LingarrDbContext dbContext,
        TimeProvider? timeProvider = null)
    {
        _hubContext = hubContext;
        _dbContext = dbContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task Emit(TranslationRequest translationRequest, int progress)
    {
        var now = _timeProvider.GetUtcNow();
        var state = GetState(translationRequest.Id);
        var terminal = translationRequest.Status is TranslationStatus.Completed
            or TranslationStatus.Failed
            or TranslationStatus.Cancelled
            or TranslationStatus.Interrupted
            or TranslationStatus.Paused;
        var statusChanged = state.HasEmitted && state.LastStatus != translationRequest.Status;

        if (state.HasEmitted && !terminal && !statusChanged && progress < 100
            && now - state.LastEmitAt < ProgressMinInterval
            && Math.Abs(progress - state.LastProgress) < ProgressMinDelta)
        {
            // throttled out: keep the tracked entity fresh so the next
            // SaveChanges (line flush or terminal emit) persists it
            translationRequest.Progress = Math.Max(translationRequest.Progress, progress);
            return;
        }

        // persist the progress so the queue screen can restore track bars
        // (and estimate ETAs) after reloads, not just for live SignalR clients
        translationRequest.Progress = Math.Max(translationRequest.Progress, progress);
        await _dbContext.SaveChangesAsync();
        if (terminal && state.PendingLineBroadcasts.Count > 0)
        {
            // the buffered line tail precedes the terminal signal so live
            // detail views append the last rows before the completion status
            await SendLineBroadcastAsync(translationRequest.Id, state);
        }
        await SendAsync(translationRequest.Id, "RequestProgress", new
        {
            translationRequest.Id,
            translationRequest.JobId,
            translationRequest.CompletedAt,
            translationRequest.ErrorMessage,
            translationRequest.StackTrace,
            Status = translationRequest.Status.GetDisplayName(),
            Progress = progress
        });
        state.LastEmitAt = now;
        state.LastProgress = progress;
        state.LastStatus = translationRequest.Status;
        state.HasEmitted = true;

        if (terminal)
        {
            await FlushLinesAsync(translationRequest.Id);
            _states.Remove(translationRequest.Id);
        }
    }

    /// <inheritdoc />
    public async Task EmitLine(
        TranslationRequest request,
        int position,
        string source,
        string target,
        string? service = null,
        LanguagePair? pair = null)
    {
        var fallback = GetFallbackPair(pair);
        var state = GetState(request.Id);
        state.PendingLines.Add(new TranslationRequestLine
        {
            TranslationRequestId = request.Id,
            Position = position,
            Source = source,
            Target = target,
            Service = service,
            MatchedSource = fallback?.Source,
            MatchedTarget = fallback?.Target,
            Tier = fallback?.Tier
        });

        BufferLineBroadcast(state, LinePayload(request, position, source, target, service, fallback));
        await FlushLineBroadcastsIfNeededAsync(request.Id, state);

        if (state.PendingLines.Count >= LineFlushBatchSize)
        {
            await FlushLinesAsync(request.Id);
        }
    }

    /// <inheritdoc />
    public async Task EmitLines(TranslationRequest request, List<TranslatedLineData> lines)
    {
        var state = GetState(request.Id);
        foreach (var line in lines)
        {
            var fallback = GetFallbackPair(line.Pair);
            state.PendingLines.Add(new TranslationRequestLine
            {
                TranslationRequestId = request.Id,
                Position = line.Position,
                Source = line.Source,
                Target = line.Target,
                Service = line.Service,
                MatchedSource = fallback?.Source,
                MatchedTarget = fallback?.Target,
                Tier = fallback?.Tier
            });

            BufferLineBroadcast(
                state, LinePayload(request, line.Position, line.Source, line.Target, line.Service, fallback));
            await FlushLineBroadcastsIfNeededAsync(request.Id, state);
        }

        if (state.PendingLines.Count >= LineFlushBatchSize)
        {
            await FlushLinesAsync(request.Id);
        }
    }

    /// <inheritdoc />
    public async Task EmitProofreadLine(
        TranslationRequest request,
        int position,
        string source,
        string target,
        string? service)
    {
        var line = await _dbContext.TranslationRequestLines
            .Where(requestLine => requestLine.TranslationRequestId == request.Id && requestLine.Position == position)
            .OrderByDescending(requestLine => requestLine.Id)
            .FirstOrDefaultAsync();

        if (line == null)
        {
            _dbContext.TranslationRequestLines.Add(new TranslationRequestLine
            {
                TranslationRequestId = request.Id,
                Position = position,
                Source = source,
                Target = target,
                Service = service
            });
        }
        else
        {
            line.Target = target;
            if (service != null)
            {
                line.Service = service;
            }
        }
        await _dbContext.SaveChangesAsync();

        await SendAsync(request.Id, "LineProofread", new
        {
            request.Id,
            Position = position,
            Source = source,
            Target = target,
            Service = service
        });
    }

    /// <summary>
    /// Writes the buffered lines of a request in one batch. Rows enter the
    /// change tracker only here, so intermediate progress saves never leak
    /// half-batched lines.
    /// </summary>
    private async Task FlushLinesAsync(int requestId)
    {
        if (!_states.TryGetValue(requestId, out var state) || state.PendingLines.Count == 0)
        {
            return;
        }

        var lines = state.PendingLines.ToArray();
        state.PendingLines.Clear();
        _dbContext.TranslationRequestLines.AddRange(lines);
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Sends one payload to the request's own group only. The global
    /// <c>TranslationRequests</c> group is reserved for the
    /// <c>ActiveTranslations</c> summary — per-request traffic never lands
    /// there (P2 broadcast collapse).
    /// </summary>
    private Task SendAsync(int requestId, string method, object payload)
    {
        return _hubContext.Clients.Group(RequestGroupPrefix + requestId)
            .SendAsync(method, payload);
    }

    /// <summary>Builds one wire payload for a translated line (per-item shape of
    /// the <c>LineTranslatedBatch</c> array — identical to the retired per-line
    /// <c>LineTranslated</c> message, so consumers keep their payload contract).</summary>
    private static object LinePayload(
        TranslationRequest request,
        int position,
        string source,
        string target,
        string? service,
        LanguagePair? fallback) => new
    {
        request.Id,
        Position = position,
        Source = source,
        Target = target,
        Service = service,
        MatchedSource = fallback?.Source,
        MatchedTarget = fallback?.Target,
        Tier = fallback?.Tier.ToString()
    };

    private void BufferLineBroadcast(RequestProgressState state, object payload)
    {
        if (state.PendingLineBroadcasts.Count == 0)
        {
            state.LineBroadcastWindowOpenedAt = _timeProvider.GetUtcNow();
        }
        state.PendingLineBroadcasts.Add(payload);
    }

    /// <summary>
    /// Flushes the buffered line payloads when the batch is full or the
    /// broadcast window has elapsed. The window is evaluated lazily on the
    /// next buffered line — no timers — so a stalled run simply holds its
    /// tail until the next line or the terminal emit.
    /// </summary>
    private async Task FlushLineBroadcastsIfNeededAsync(int requestId, RequestProgressState state)
    {
        if (state.PendingLineBroadcasts.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (state.PendingLineBroadcasts.Count < LineBroadcastBatchSize
            && now - state.LineBroadcastWindowOpenedAt < LineBroadcastWindow)
        {
            return;
        }

        await SendLineBroadcastAsync(requestId, state);
    }

    private async Task SendLineBroadcastAsync(int requestId, RequestProgressState state)
    {
        var lines = state.PendingLineBroadcasts.ToArray();
        state.PendingLineBroadcasts.Clear();
        await SendAsync(requestId, "LineTranslatedBatch", lines);
    }

    private RequestProgressState GetState(int requestId) =>
        _states.TryGetValue(requestId, out var state) ? state : _states[requestId] = new RequestProgressState();

    /// <summary>
    /// Returns the pair when the service translated against a fallback variant of the requested
    /// codes, or null when the match was exact (so no fallback columns need to be persisted).
    /// </summary>
    private static LanguagePair? GetFallbackPair(LanguagePair? pair)
    {
        if (pair == null || pair.Tier == MatchTier.Exact)
        {
            return null;
        }
        return pair;
    }

    private sealed class RequestProgressState
    {
        public DateTimeOffset LastEmitAt { get; set; }
        public int LastProgress { get; set; }
        public TranslationStatus? LastStatus { get; set; }
        public bool HasEmitted { get; set; }
        public List<TranslationRequestLine> PendingLines { get; } = [];
        public List<object> PendingLineBroadcasts { get; } = [];
        public DateTimeOffset LineBroadcastWindowOpenedAt { get; set; }
    }
}
