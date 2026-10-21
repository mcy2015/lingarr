using Lingarr.Core.Configuration;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.SubtitleSearch.Pipeline;

/// <summary>
/// B08 single source of truth for "which target languages does one media
/// item want subtitles in". Resolution order: the per-media override column
/// (<c>movies.target_languages_override</c> / <c>shows.target_languages_override</c>)
/// wins over the global <c>target_languages</c> setting; a NULL column
/// follows the global list; an EMPTY stored string is the explicit
/// 「无目标」state (the media wants no subtitles — reversible any time, kept
/// semantically apart from the <c>no_subtitle_needed</c> audio exemption).
/// Episodes carry no column of their own — they inherit their show's
/// override.
/// <para>
/// Shared by every writer of the needs_translation / no_subtitle_needed
/// flags (<see cref="SubtitleCoverageRefresher"/>, StatisticsJob, the
/// subtitle-need endpoint) and the library detail projection, so the
/// priority-queue's gap INSERT — which selects on those flags — inherits the
/// same verdict without re-deriving it in SQL.
/// </para>
/// </summary>
public sealed class TargetLanguageResolver
{
    private readonly ISettingService _settingService;

    public TargetLanguageResolver(ISettingService settingService)
    {
        _settingService = settingService;
    }

    /// <summary>
    /// The global target code list from settings, verbatim (code order
    /// preserved — the same list every pre-B08 consumer derived here).
    /// </summary>
    public async Task<List<string>> GetGlobalAsync(CancellationToken cancellationToken = default) =>
        (await _settingService.GetSettingAsJson<TargetLanguage>(
             SettingKeys.Translation.TargetLanguages)
         ?? [])
        .Select(language => language.Code)
        .ToList();

    /// <summary>
    /// Media-level resolution: override &gt; global. A NULL override column
    /// follows the global list (pre-B08 behavior); an override value —
    /// including the EMPTY string, the explicit「无目标」state — replaces it
    /// entirely (a partial override never merges with the global list).
    /// </summary>
    public static List<string> Resolve(string? mediaOverride, IReadOnlyCollection<string> globalTargets) =>
        mediaOverride is null
            ? [.. globalTargets]
            : ParseStored(mediaOverride);

    /// <summary>
    /// The stored comma-separated form → codes: split, trim, drop empties,
    /// case-fold, dedupe. <c>""</c> parses to the empty set (explicit
    /// no-target) — never to NULL-follows-global.
    /// </summary>
    public static List<string> ParseStored(string stored) =>
        stored
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// Codes → the stored comma-separated form. An empty code list stores
    /// <c>""</c> (explicit no-target), never NULL — NULL stays reserved for
    /// "follow global" and is only ever written by the clear-override path.
    /// </summary>
    public static string SerializeOverride(IEnumerable<string> codes) =>
        string.Join(',',
            codes.Select(code => code.Trim().ToLowerInvariant())
                .Where(code => code.Length > 0)
                .Distinct());
}
