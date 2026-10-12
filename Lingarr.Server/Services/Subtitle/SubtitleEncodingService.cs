using System.Text;
using Lingarr.Server.Interfaces.Services.Subtitle;

namespace Lingarr.Server.Services.Subtitle;

/// <summary>
/// Encoding detection backed by the Mozilla charset model (Ude) with a deterministic
/// fast path: byte-order marks first, then a strict UTF-8 validation — both cover the
/// overwhelming majority of real files without running the detector. Everything else
/// (GBK, Big5, Shift-JIS, Windows-1252 …) is delegated to the detector; undetectable
/// bytes fall back to Latin-1 so conversion never fails outright.
/// </summary>
public class SubtitleEncodingService : ISubtitleEncodingService
{
    private static readonly Encoding Utf8Strict =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static SubtitleEncodingService()
    {
        // big5 / windows-1252 and friends are not in-box on .NET Core; the code pages
        // provider supplies them (GB18030 is in-box but resolving through the same
        // registry keeps one code path).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <inheritdoc />
    public Encoding Detect(byte[] content)
    {
        if (content.Length == 0)
        {
            return Utf8;
        }

        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        try
        {
            Utf8Strict.GetString(content);
            return Utf8;
        }
        catch (DecoderFallbackException)
        {
            // not UTF-8 — hand over to the charset detector
        }

        var detector = new Ude.CharsetDetector();
        detector.Feed(content, 0, content.Length);
        detector.DataEnd();

        if (detector.Charset is null)
        {
            return Encoding.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(detector.Charset);
        }
        catch (ArgumentException)
        {
            // detector named an encoding this platform cannot resolve — last resort
            return Encoding.Latin1;
        }
    }

    /// <inheritdoc />
    public byte[] ConvertToUtf8(byte[] content)
    {
        if (content.Length == 0)
        {
            return content;
        }

        var encoding = Detect(content);
        var text = encoding.GetString(content);

        // GetString keeps a decoded BOM as U+FEFF; players and parsers don't want it.
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return Utf8.GetBytes(text);
    }
}
