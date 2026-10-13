using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Forked from: https://github.com/AlexPoint/SubtitlesParser
/// Writer for the .srt subtitle files
/// </summary>
public class SrtWriter : ISubtitleWriter
{
    /// <summary>
    /// Converts a subtitle item into the lines for an SRT subtitle entry
    /// </summary>
    /// <param name="subtitleItem">The SubtitleItem to convert</param>
    /// <param name="subtitleEntryNumber">The subtitle number for the entry (increments sequentially from 1)</param>
    /// <param name="bilingual">Whether to append the original line below the translation</param>
    /// <returns>A list of strings to write as an SRT subtitle entry</returns>
    private IEnumerable<string> SubtitleItemToSubtitleEntry(SubtitleItem subtitleItem, int subtitleEntryNumber, bool bilingual)
    {
        // take the start and end timestamps and format it as a timecode line
        string FormatTimeCodeLine()
        {
            TimeSpan start = TimeSpan.FromMilliseconds(subtitleItem.StartTime);
            TimeSpan end = TimeSpan.FromMilliseconds(subtitleItem.EndTime);
            return $"{start:hh\\:mm\\:ss\\,fff} --> {end:hh\\:mm\\:ss\\,fff}";
        }

        List<string> lines = new List<string>();
        lines.Add(subtitleEntryNumber.ToString());
        lines.Add(FormatTimeCodeLine());
        lines.AddRange(subtitleItem.TranslatedLines);

        if (bilingual && subtitleItem.TranslatedLines.Any(l => !string.IsNullOrEmpty(l)))
        {
            var original = GetBilingualOriginalLine(subtitleItem);
            if (!string.IsNullOrWhiteSpace(original))
            {
                lines.Add(original);
            }
        }

        return lines;
    }

    /// <summary>
    /// Builds the original-text line appended below the translation for bilingual output.
    /// The original is used in plain-text form and merged into a single line.
    /// </summary>
    private static string GetBilingualOriginalLine(SubtitleItem subtitleItem)
    {
        var source = subtitleItem.PlaintextLines.Count > 0
            ? subtitleItem.PlaintextLines
            : subtitleItem.Lines;
        return string.Join(" ", source);
    }
    
    /// <inheritdoc />
    public async Task WriteStreamAsync(
        Stream stream, 
        IEnumerable<SubtitleItem> subtitleItems, 
        bool stripSubtitleFormatting,
        bool bilingual = false)
    {
        try
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream), "Stream cannot be null");
            }

            if (!stream.CanWrite)
            {
                throw new InvalidOperationException("Stream is not writable.");
            }

            await using TextWriter writer = new StreamWriter(stream);

            List<SubtitleItem>
                items = subtitleItems.ToList(); // avoid multiple enumeration since we're using a for instead of foreach
            for (int index = 0; index < items.Count; index++)
            {
                SubtitleItem subtitleItem = items[index];
                // Create a subtitle entry
                IEnumerable<string>
                    lines = SubtitleItemToSubtitleEntry(subtitleItem,
                        subtitleItem.Position, bilingual); // add one because subtitle entry numbers start at 1 instead of 0
                foreach (string line in lines)
                {
                    await writer.WriteLineAsync(line);
                }

                await writer.WriteLineAsync(); // empty line between subtitle entries
            }

            await writer.FlushAsync();
        }
        catch (IOException)
        {
            // Propagate: callers (SubtitleService/jobs) own failure handling and logging.
            throw;
        }
    }
}