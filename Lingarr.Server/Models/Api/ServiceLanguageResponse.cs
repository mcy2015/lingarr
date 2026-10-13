using Lingarr.Contracts.Models;

namespace Lingarr.Server.Models.Api;

/// <summary>
/// Language capability of one translation service (PM analysis P1-3,
/// language-policy card). Status semantics:
/// "restricted" — a concrete language table gates the pair;
/// "unrestricted" — no language list (AI-style), any culture-valid pair;
/// "unknown" — the table could not be retrieved (missing key, offline).
/// </summary>
public class ServiceLanguageResponse
{
    public required string ServiceType { get; set; }
    public required string Status { get; set; }
    public List<SourceLanguage> Languages { get; set; } = [];
    public string? Message { get; set; }
}
