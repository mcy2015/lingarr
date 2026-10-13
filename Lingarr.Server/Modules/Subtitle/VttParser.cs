using System.Text;
using System.Text.RegularExpressions;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Parser for WebVTT (.vtt) subtitle files. Accepts the WEBVTT header block
/// (with optional metadata), optional cue identifiers and both '.' and ','
/// millisecond separators for robustness against SRT-style hand-edits.
/// </summary>
public class VttParser : ISubtitleParser
{
    private static readonly Regex TimeCodeRegex =
        new(@"(?:([0-9]+):)?([0-9]+):([0-9]+)(?:[.,]([0-9]+))?", RegexOptions.Compiled);

    private static readonly Regex CueTimingRegex =
        new(@"-->", RegexOptions.Compiled);

    public List<SubtitleItem> ParseStream(Stream subtitleStream, Encoding encoding)
    {
        if (!subtitleStream.CanRead || !subtitleStream.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable");
        }

        subtitleStream.Position = 0;
        using var reader = new StreamReader(subtitleStream, encoding, true);
        var subtitles = new List<SubtitleItem>();
        var position = 1;
        var block = new List<string>();

        // The WEBVTT signature line and any header metadata before the first
        // blank line are file-level preamble, not cue content.
        var header = reader.ReadLine();
        if (header is null || !header.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Missing WEBVTT header");
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (TryParseBlock(block, position, out var item))
                {
                    subtitles.Add(item);
                    position++;
                }
                block.Clear();
                continue;
            }
            block.Add(line.Trim());
        }

        if (TryParseBlock(block, position, out var last))
        {
            subtitles.Add(last);
        }

        if (subtitles.Count == 0)
        {
            throw new FormatException("No valid subtitles found in the stream");
        }

        return subtitles;
    }

    private static bool TryParseBlock(List<string> block, int fallbackPosition, out SubtitleItem item)
    {
        item = new SubtitleItem();
        if (block.Count == 0)
        {
            return false;
        }

        // Cue ids are optional in WebVTT; the timing line is the anchor.
        var timingIndex = block.FindIndex(l => CueTimingRegex.IsMatch(l));
        if (timingIndex is -1 or > 1)
        {
            return false;
        }

        if (!TryParseTimeCodes(block[timingIndex], out var start, out var end))
        {
            return false;
        }

        item.StartTime = start;
        item.EndTime = end;
        item.Position = timingIndex == 1 && int.TryParse(block[0], out var id) && id > 0
            ? id
            : fallbackPosition;

        foreach (var text in block.Skip(timingIndex + 1))
        {
            // Skip voice tags and VTT settings carried after the timestamps.
            if (text.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) || text.Contains("-->"))
            {
                continue;
            }
            item.Lines.Add(text);
            item.PlaintextLines.Add(SubtitleFormatterService.RemoveMarkup(text));
        }

        return item.Lines.Count > 0;
    }

    private static bool TryParseTimeCodes(string line, out int start, out int end)
    {
        start = end = -1;
        var parts = line.Split("-->", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        // ParseTimeCode anchors on the first time pattern, so leading spaces
        // and trailing cue settings ("align:start …") are ignored naturally.
        start = ParseTimeCode(parts[0]);
        end = ParseTimeCode(parts[1]);
        if (start >= 0 && end >= 0 && end < start)
        {
            (start, end) = (end, start);
        }

        return start >= 0 && end >= 0;
    }

    /// <summary>Hours are optional in WebVTT timecodes (MM:SS.mmm).</summary>
    private static int ParseTimeCode(string timeCode)
    {
        var match = TimeCodeRegex.Match(timeCode.Trim());
        if (!match.Success)
        {
            return -1;
        }

        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
        var minutes = int.Parse(match.Groups[2].Value);
        var seconds = int.Parse(match.Groups[3].Value);
        var milliseconds = match.Groups[4].Success
            ? int.Parse(match.Groups[4].Value.PadRight(3, '0')[..3])
            : 0;

        if (minutes >= 60 || seconds >= 60)
        {
            return -1;
        }

        return hours * 3600000 + minutes * 60000 + seconds * 1000 + milliseconds;
    }
}
