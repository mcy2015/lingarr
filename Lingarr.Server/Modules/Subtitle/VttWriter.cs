using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Writer for WebVTT (.vtt) subtitle files: WEBVTT header, dot millisecond
/// separators and optional cue numbering, mirroring the SRT writer's
/// bilingual behaviour (original line appended below the translation).
/// </summary>
public class VttWriter : ISubtitleWriter
{
    public async Task WriteStreamAsync(
        Stream stream,
        IEnumerable<SubtitleItem> subtitleItems,
        bool stripSubtitleFormatting,
        bool bilingual = false)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream), "Stream cannot be null");
        }

        if (!stream.CanWrite)
        {
            throw new InvalidOperationException("Stream is not writable.");
        }

        await using TextWriter writer = new StreamWriter(stream);
        await writer.WriteLineAsync("WEBVTT");
        await writer.WriteLineAsync();

        foreach (var item in subtitleItems)
        {
            var start = TimeSpan.FromMilliseconds(item.StartTime);
            var end = TimeSpan.FromMilliseconds(item.EndTime);
            await writer.WriteLineAsync(item.Position.ToString());
            await writer.WriteLineAsync($"{start:hh\\:mm\\:ss\\.fff} --> {end:hh\\:mm\\:ss\\.fff}");
            foreach (var line in item.TranslatedLines)
            {
                await writer.WriteLineAsync(line);
            }

            if (bilingual && item.TranslatedLines.Any(l => !string.IsNullOrEmpty(l)))
            {
                var source = item.PlaintextLines.Count > 0 ? item.PlaintextLines : item.Lines;
                var original = string.Join(" ", source);
                if (!string.IsNullOrWhiteSpace(original))
                {
                    await writer.WriteLineAsync(original);
                }
            }

            await writer.WriteLineAsync();
        }

        await writer.FlushAsync();
    }
}
