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

    // Curated common-hanzi set (high-frequency simp+trad plus subtitle vocabulary).
    // Used to score CJK plausibility when the detector names a single-byte charset:
    // on short ASCII-heavy files it can misread GBK as KOI8-R/windows-1252, which
    // would mojibake the user's data; decoding as GB18030/Big5 and counting common
    // hanzi catches that reliably.
    private const string CommonHanzi =
        "的一是不了在人有我他这个们中来上大为和国地到以说时要就出会可也你对生能而子那得于着下自之年过发后作里用道行所然家种事成方多经么去法学如都同现当没动面起看定天分还进好小部其些主样理心她本前开但因只从想实日军者意无力它与长把机十民第公此已工使情明性知全三又关点正业外将两高间由问很最重并物手应战向头文体政美相见被利什二等产或新己制身果加西斯月话合回特代内信表化老给世位次度门任常先海通教儿原东声提立及比员解水名真论处走义各入几口认条平系气题活尔更别打女变四神总何电数安少报才结反受目太量再感建务做接必场件计管期市直德资命山金指克许统区保至队形社便空决治展马科司五基眼书非则听白却界达光放强即像难且权思王象完设式色路记南品住告类求据程北边死张该交规万取拉格望觉术领共确传师观清今切院让识候带导争运笑飞风步改收根干造言联持组每济车亲极林服快办议往元英士证近失转夫令准布始怎呢存未远叫台单影具罗字爱击流备兵连调深商算质团集百需价花党华城石级整府离况亚请技际约示复病息究线似官火断精满支视消越器容照须九增研写称企八功包片史委乎查" +
        "字幕简体繁體時間軸校正視覺廳這傳統測試影視劇場對白翻譯語言電影集季话話";

    private const double MinCjkPlausibility = 0.34;
    private const int MinNonAsciiForOverride = 4;

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

        // Single-byte verdicts are untrustworthy for multibyte content: short GBK
        // files get read as KOI8-R/windows-1252. When a CJK interpretation scores
        // clearly plausible, it wins over the detector's guess.
        var cjkOverride = TryCjkOverride(content, detector.Charset);
        if (cjkOverride is not null)
        {
            return cjkOverride;
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

    /// <summary>
    /// When the detector named a single-byte charset, scores GB18030 vs Big5 decodes
    /// by common-hanzi ratio and returns the winner if it clears the plausibility bar.
    /// Multibyte verdicts (UTF-8/16, gb18030, big5, EUC/Shift-JIS/EUC-KR) are trusted as-is.
    /// </summary>
    private static Encoding? TryCjkOverride(byte[] content, string detectedCharset)
    {
        if (detectedCharset.StartsWith("UTF", StringComparison.OrdinalIgnoreCase) ||
            detectedCharset.StartsWith("EUC", StringComparison.OrdinalIgnoreCase) ||
            detectedCharset.StartsWith("ISO-2022", StringComparison.OrdinalIgnoreCase) ||
            detectedCharset.Contains("Shift", StringComparison.OrdinalIgnoreCase) ||
            detectedCharset.Equals("gb18030", StringComparison.OrdinalIgnoreCase) ||
            detectedCharset.Equals("big5", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gb = TryDecode(content, "GB18030");
        var big5 = TryDecode(content, "big5");
        if (gb is null && big5 is null)
        {
            return null;
        }

        var (gbNonAscii, gbScore) = Plausibility(gb);
        var (big5NonAscii, big5Score) = Plausibility(big5);
        var (bestScore, bestName, bestNonAscii) = gbScore >= big5Score
            ? (gbScore, "GB18030", gbNonAscii)
            : (big5Score, "big5", big5NonAscii);

        return bestNonAscii >= MinNonAsciiForOverride && bestScore >= MinCjkPlausibility
            ? Encoding.GetEncoding(bestName)
            : null;
    }

    private static string? TryDecode(byte[] content, string encodingName)
    {
        try
        {
            return Encoding.GetEncoding(encodingName).GetString(content);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Fraction of non-ASCII characters that are common hanzi (ASCII structure ignored).</summary>
    private static (int NonAscii, double Score) Plausibility(string? decoded)
    {
        if (decoded is null)
        {
            return (0, 0);
        }

        int nonAscii = 0, common = 0;
        foreach (var c in decoded)
        {
            if (c < 0x80)
            {
                continue;
            }

            nonAscii++;
            if (c is >= (char)0x4E00 and <= (char)0x9FFF && CommonHanzi.Contains(c))
            {
                common++;
            }
        }

        return (nonAscii, nonAscii == 0 ? 0 : (double)common / nonAscii);
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
