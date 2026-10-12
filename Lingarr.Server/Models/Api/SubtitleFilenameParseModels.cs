using Lingarr.Server.Services.Subtitle;

namespace Lingarr.Server.Models.Api;

/// <summary>Request body for filename parsing in the import workbench preview.</summary>
public class SubtitleFilenameParseRequest
{
    public required List<string> Filenames { get; set; }
}

/// <summary>Season/episode/language hints parsed out of one subtitle file name.</summary>
public class SubtitleFilenameParseResult
{
    public required string FileName { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }

    /// <summary><see cref="SubtitleMatchTier"/> name: Exact, Guess or None.</summary>
    public string MatchTier { get; set; } = nameof(SubtitleMatchTier.None);

    public List<string> LanguageTags { get; set; } = [];
}
