using System.Text;

namespace Lingarr.Server.Interfaces.Services.Subtitle;

/// <summary>
/// Detects the text encoding of raw subtitle bytes and transcodes them to UTF-8.
/// Import sources (fan-sub archives, legacy downloads) are frequently GBK/Big5/Latin-1
/// encoded; players and the translation pipeline all expect UTF-8.
/// </summary>
public interface ISubtitleEncodingService
{
    /// <summary>
    /// Detects the encoding of raw subtitle bytes.
    /// Order: BOM → strict UTF-8 (covers ASCII) → charset detection (GBK/Big5/…) → Latin-1 fallback.
    /// Never throws for decidable input; undetectable bytes fall back to Latin-1.
    /// </summary>
    Encoding Detect(byte[] content);

    /// <summary>
    /// Transcodes raw subtitle bytes to UTF-8 without BOM, decoding via <see cref="Detect"/>.
    /// A leading zero-width no-break space (BOM decoded) is stripped.
    /// </summary>
    byte[] ConvertToUtf8(byte[] content);
}
