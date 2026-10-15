namespace Lingarr.Server.Models.FileSystem;

public class Subtitles
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Primary (first) language code — kept for single-language consumers.
    /// Prefer <see cref="Languages"/>, which carries every language a
    /// dual-language file (e.g. scjp / 简日) provides.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>All language codes found in the filename tag, canonicalised.</summary>
    public List<string> Languages { get; set; } = [];

    public string Caption { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
}