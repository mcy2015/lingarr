namespace Lingarr.Server.Models.Api;

/// <summary>
/// Tray estimate (A5): rough source-character volume for translating the
/// selected media into a target language, priced at the default provider's
/// rate. Cost is null when that provider has no rate configured.
/// </summary>
public class TranslationEstimate
{
    public long Characters { get; init; }

    /// <summary>Provider whose rate priced the estimate; empty when unset.</summary>
    public required string Provider { get; init; }

    public double? Cost { get; init; }
}
