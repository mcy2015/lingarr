using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Services;

/// <summary>
/// Facade over the translation request sub-services. Keeps the public contract of the
/// former god-service stable for controllers and jobs; the implementation lives in
/// <see cref="TranslationRequestQueryService"/>, <see cref="TranslationRequestQueueService"/>,
/// <see cref="ProofreadRequestService"/> and <see cref="ContentTranslationService"/>.
/// </summary>
public class TranslationRequestService : ITranslationRequestService
{
    private readonly ITranslationRequestQueryService _queryService;
    private readonly ITranslationRequestQueueService _queueService;
    private readonly IProofreadRequestService _proofreadRequestService;
    private readonly IContentTranslationService _contentTranslationService;

    public TranslationRequestService(
        ITranslationRequestQueryService queryService,
        ITranslationRequestQueueService queueService,
        IProofreadRequestService proofreadRequestService,
        IContentTranslationService contentTranslationService)
    {
        _queryService = queryService;
        _queueService = queueService;
        _proofreadRequestService = proofreadRequestService;
        _contentTranslationService = contentTranslationService;
    }

    /// <inheritdoc />
    public Task<TranslationRequestDetail?> GetTranslationRequest(int id)
        => _queryService.GetTranslationRequest(id);

    /// <inheritdoc />
    public Task<int> CreateRequest(TranslateAbleSubtitle translateAbleSubtitle)
        => _queueService.CreateRequest(translateAbleSubtitle);

    /// <inheritdoc />
    public Task<int> CreateImportRequest(
        string subtitlePath,
        string title,
        string sourceLanguage,
        string targetLanguage,
        int? mediaId,
        MediaType mediaType,
        string? contentHash = null)
        => _queueService.CreateImportRequest(subtitlePath, title, sourceLanguage, targetLanguage, mediaId, mediaType, contentHash);

    /// <inheritdoc />
    public Task CreateBulkRequest(BulkTranslateRequest request)
        => _queueService.CreateBulkRequest(request);

    /// <inheritdoc />
    public Task<List<ActiveTranslation>> GetActiveTranslations()
        => _queryService.GetActiveTranslations();

    /// <inheritdoc />
    public Task<List<ActiveTranslation>> UpdateActiveCount()
        => _queryService.UpdateActiveCount();

    /// <inheritdoc />
    public Task ResumeTranslationRequests()
        => _queueService.ResumeTranslationRequests();

    /// <inheritdoc />
    public Task<PagedResult<TranslationRequestSummary>> GetTranslationRequests(
        string? searchQuery,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize)
        => _queryService.GetTranslationRequests(searchQuery, orderBy, ascending, pageNumber, pageSize);

    /// <inheritdoc />
    public Task<string?> RemoveTranslationRequest(TranslationRequest cancelRequest)
        => _queueService.RemoveTranslationRequest(cancelRequest);

    /// <inheritdoc />
    public Task<string?> RetryTranslationRequest(TranslationRequest retryRequest)
        => _queueService.RetryTranslationRequest(retryRequest);

    /// <inheritdoc />
    public Task<string?> ResumeTranslationRequest(TranslationRequest resumeRequest)
        => _queueService.ResumeTranslationRequest(resumeRequest);

    /// <inheritdoc />
    public Task<ProofreadStatusResponse> GetProofreadStatus()
        => _proofreadRequestService.GetProofreadStatus();

    /// <inheritdoc />
    public Task<string?> ProofreadLine(
        ProofreadLineRequest proofreadLineRequest,
        CancellationToken cancellationToken)
        => _proofreadRequestService.ProofreadLine(proofreadLineRequest, cancellationToken);

    /// <inheritdoc />
    public Task<string?> ProofreadTranslationRequest(TranslationRequest proofreadRequest)
        => _proofreadRequestService.ProofreadTranslationRequest(proofreadRequest);

    /// <inheritdoc />
    public Task<string?> ApplyProofreadLine(ProofreadLineApplyRequest applyRequest)
        => _proofreadRequestService.ApplyProofreadLine(applyRequest);

    /// <inheritdoc />
    public Task<string?> CancelTranslationRequest(TranslationRequest cancelRequest)
        => _queueService.CancelTranslationRequest(cancelRequest);

    /// <inheritdoc />
    public Task ClearMediaHash(TranslationRequest translationRequest)
        => _queueService.ClearMediaHash(translationRequest);

    /// <inheritdoc />
    public Task<TranslationRequest> UpdateTranslationRequest(TranslationRequest translationRequest,
        TranslationStatus status, string? jobId = null)
        => _queueService.UpdateTranslationRequest(translationRequest, status, jobId);

    /// <inheritdoc />
    public Task<BatchSubtitleLine[]> TranslateContentAsync(
        TranslateAbleSubtitleContent translateAbleContent,
        CancellationToken parentCancellationToken)
        => _contentTranslationService.TranslateContentAsync(translateAbleContent, parentCancellationToken);
}
