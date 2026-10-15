using Lingarr.Contracts.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Defines a service for collecting subtitle files.
/// </summary>
public interface ISubtitleService
{
    /// <summary>
    /// Collects subtitle files from a specified directory path and organizes them into a list of <see cref="Subtitles"/> objects.
    /// </summary>
    /// <param name="path">The relative path from the media root directory where subtitle files (.srt) are located. The path should start with a forward slash ("/").</param>
    /// <returns>
    /// A <see cref="List{Subtitles}"/> containing the collected subtitle files. Each <see cref="Subtitles"/> object includes the file path,
    /// file name (excluding language code), and detected language code extracted from the file name. If the language code cannot be determined
    /// from the file name, the file content is sniffed (first 64 KB, encoding-normalised) and a confident language replaces "unknown";
    /// ambiguous content stays "unknown".
    /// </returns>
    Task<List<Subtitles>> GetAllSubtitles(string path);

    /// <summary>
    /// Reads and parses a subtitle file.
    /// </summary>
    /// <param name="filePath">The path to the subtitle file to be read.</param>
    /// <returns>A list of SubtitleItem objects representing the parsed subtitles.</returns>
    Task<List<SubtitleItem>> ReadSubtitles(string filePath);

    /// <summary>
    /// Writes a list of subtitle items to a file.
    /// </summary>
    /// <param name="filePath">The path where the subtitle file will be written.</param>
    /// <param name="subtitles">The list of SubtitleItem objects to be written to the file.</param>
    /// <param name="stripSubtitleFormatting">Boolean used for indicating that styles need to be stripped from the subtitle</param>
    Task WriteSubtitles(string filePath, List<SubtitleItem> subtitles, bool stripSubtitleFormatting, bool bilingual = false);

    /// <summary>
    /// Creates a new file path for a translated subtitle file, expanding the
    /// <c>subtitle_naming_template</c> setting (default "{原名}.{语言}").
    /// </summary>
    /// <param name="originalPath">The original path of the subtitle file.</param>
    /// <param name="targetLanguage">The target language code to be added to the file name.</param>
    /// <param name="subtitleTag">Subtitle tag to be appended after the template.</param>
    /// <returns>A new file path built from the naming template, with the output format extension appended.</returns>
    Task<string> CreateFilePath(string originalPath, string targetLanguage, string subtitleTag);
    
    /// <summary>
    /// Removes overlaps between consecutive subtitles by trimming end times, start times are never shifted.
    /// </summary>
    List<SubtitleItem> FixOverlappingSubtitles(List<SubtitleItem> subtitles);

    /// <summary>
    /// Adds an introductory subtitle at the beginning that identifies the translation service used.
    /// The intro duration is automatically adjusted to avoid overlapping with existing subtitles.
    /// </summary>
    /// <param name="serviceType">The translation service type (e.g., "openai", "google").</param>
    /// <param name="translatedSubtitles">The subtitle list to prepend the intro to.</param>
    /// <param name="translationService">The service instance used to extract model name if available.</param>
    void AddTranslatorInfo(string serviceType, List<SubtitleItem> translatedSubtitles,
        ITranslationService translationService);
    
    
    bool ValidateSubtitle(
        string filePath,
        SubtitleValidationOptions validationOptions);

    /// <summary>
    /// Gets all subtitles from the given path that match the specified file name.
    /// </summary>
    /// <param name="path">The directory path to search for subtitle files.</param>
    /// <param name="fileName">The media file name to match against subtitle file names.</param>
    /// <returns>A list of matching subtitle files.</returns>
    Task<List<Subtitles>> GetSubtitles(string path, string fileName);

    /// <summary>
    /// Selects the source subtitle from matching subtitles by finding the first available source language
    /// and selecting the appropriate subtitle file, respecting the ignoreCaptions setting.
    /// </summary>
    /// <param name="subtitles">The list of subtitle files to search.</param>
    /// <param name="sourceCodes">The set of acceptable source language codes.</param>
    /// <param name="ignoreCaptions">The ignoreCaptions setting value.</param>
    /// <returns>A <see cref="SelectedSourceSubtitle"/> if a source subtitle was found, null otherwise.</returns>
    SelectedSourceSubtitle? SelectSourceSubtitle(List<Subtitles> subtitles, HashSet<string> sourceCodes, string ignoreCaptions);
}