namespace Lingarr.Server.Models;

public class TranslationRequestSubtitleLines
{
    public int Position { get; set; }
    public required string Source { get; set; }
    public required string Target { get; set; }
    public string? Service { get; set; }

    /// <summary>Cue start in milliseconds from the source subtitle; null when the file is gone.</summary>
    public long? StartTimeMs { get; set; }

    /// <summary>Cue end in milliseconds from the source subtitle; null when the file is gone.</summary>
    public long? EndTimeMs { get; set; }
}