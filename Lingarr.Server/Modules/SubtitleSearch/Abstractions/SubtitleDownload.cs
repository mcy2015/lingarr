namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Raw payload of one fetched subtitle (tech design ②): the bytes exactly as
/// downloaded — often a zip/rar archive — plus just enough metadata for the
/// download pipeline (⑦) to pick an entry, detect the encoding and choose a
/// writer. The persisted history row of the same name is
/// <c>Lingarr.Core.Entities.SubtitleDownload</c>; keep the two apart.
/// </summary>
/// <param name="Provider">Source key that produced the payload.</param>
/// <param name="FileName">Remote file name (archive or subtitle) for logs and entry picking.</param>
/// <param name="Format">Source-declared format ("srt", "ass", "zip"…); the pipeline re-detects from content.</param>
/// <param name="Content">Untouched payload bytes.</param>
/// <param name="QuotaRemaining">
/// Remaining daily quota the source reported alongside this download (the
/// assrt <c>quota_remain</c> snapshot of the producing detail round), when
/// known — feeds the provider_quota_usage telemetry through the pipeline.
/// Null (the default) keeps the source's last recorded value.
/// </param>
public sealed record SubtitleDownload(
    string Provider,
    string FileName,
    string Format,
    byte[] Content,
    int? QuotaRemaining = null);
