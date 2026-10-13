using OpenccNetLib;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Chinese variant conversion for imported subtitles, backed by OpenCC.
/// Applied at import time as an explicit per-import option (none/simplified/
/// traditional) — it alters the stored subtitle text, so it never runs by default.
/// </summary>
public static class ChineseSubtitleConverter
{
    private static readonly Opencc TraditionalConverter = new(OpenccConfig.S2T);
    private static readonly Opencc SimplifiedConverter = new(OpenccConfig.T2S);

    public static readonly string[] SupportedModes = ["none", "simplified", "traditional"];

    /// <summary>
    /// Converts text between Chinese variants. "simplified" runs Traditional→Simplified,
    /// "traditional" runs Simplified→Traditional; anything else (including null and
    /// "none") returns the input unchanged.
    /// </summary>
    public static string Convert(string text, string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "simplified" => SimplifiedConverter.Convert(text),
            "traditional" => TraditionalConverter.Convert(text),
            _ => text
        };
    }
}
