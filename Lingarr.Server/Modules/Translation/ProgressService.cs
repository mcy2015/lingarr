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
/// Persists translation progress and streams it over SignalR (scale audit P0-10).
///
/// Broadcast grouping: every message goes to the per-request group
/// (<c>TranslationRequest-{id}</c>) in addition to the legacy global
/// <c>TranslationRequests</c> group that the queue/detail screens still
/// subscribe to. A client that joins only its request's group receives just
/// that request's traffic — requests never cross groups. Sending to an empty
/// group is a no-op, so the per-request groups are free until consumers
/// migrate onto them.
///
/// Progress throttling: mid-run <see cref="Emit"/> calls are collapsed to at
/// most one message per ≥2% progress delta (or 500ms, whichever comes first);
/// the first emission, every status change and the terminal state always go
/// out. The persisted <c>Progress</c> column follows the same rhythm, which
/// also removes most of the per-percent <c>SaveChanges</c> write amplification.
///
/// Line batching: <see cref="EmitLine"/> results are buffered per request and
/// written in batches of <see cref="LineFlushBatchSize"/> rows (broadcasts
/// still stream per line, so live detail views are unchanged). Any terminal
/// <see cref="Emit"/> (completed/failed/cancelled/paused) flushes the tail, so
/// a finished request always has its full line set on disk. If the process
/// dies mid-run, the flushed batches persist and the unflushed tail is simply
/// re-translated on resume — the job's checkpoint reads persisted rows by
/// position, so already-batched lines are never duplicated.
/// </summary>
public class ProgressService : IProgressService
{
    private const string GlobalGroup = "TranslationRequests";
    private const string RequestGroupPrefix = "TranslationRequest-";
    private const int LineFlushBatchSize = 50;
    private static readonly TimeSpan ProgressMinInterval = TimeSpan.FromMilliseconds(500);
    private const int ProgressMinDelta = 2;

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

        await SendAsync(request.Id, "LineTranslated", new
        {
            request.Id,
            Position = position,
            Source = source,
            Target = target,
            Service = service,
            MatchedSource = fallback?.Source,
            MatchedTarget = fallback?.Target,
            Tier = fallback?.Tier.ToString()
        });

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

            await SendAsync(request.Id, "LineTranslated", new
            {
                request.Id,
                line.Position,
                line.Source,
                line.Target,
                line.Service,
                MatchedSource = fallback?.Source,
                MatchedTarget = fallback?.Target,
                Tier = fallback?.Tier.ToString()
            });
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
    /// Sends one payload to the request's own group and the legacy global
    /// group (existing queue/detail screens subscribe to the global one).
    /// </summary>
    private Task SendAsync(int requestId, string method, object payload)
    {
        return Task.WhenAll(
            _hubContext.Clients.Group(GlobalGroup).SendAsync(method, payload),
            _hubContext.Clients.Group(RequestGroupPrefix + requestId).SendAsync(method, payload));
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
    }
}
