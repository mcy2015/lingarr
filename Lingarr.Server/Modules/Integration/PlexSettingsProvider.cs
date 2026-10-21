using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// Resolved Plex connection settings (B03): the server base URL and the
/// X-Plex-Token used to authenticate both library calls.
/// </summary>
/// <param name="Url">Plex base URL exactly as stored (may carry a trailing slash — the service normalises).</param>
/// <param name="Token">Decrypted X-Plex-Token value.</param>
public sealed record PlexSettings(string Url, string Token);

/// <summary>
/// Plex counterpart of <see cref="IntegrationSettingsProvider"/>: the URL is
/// read as a plain setting, the token through the encrypted channel, and a
/// missing either one means "not configured" (null) — the refresh job treats
/// that as a fast success instead of an error, so unlike the *arr sync path
/// the gap is logged as a warning, not an error.
/// </summary>
public class PlexSettingsProvider
{
    private readonly ISettingService _settingService;
    private readonly ILogger<PlexSettingsProvider> _logger;

    public PlexSettingsProvider(
        ISettingService settingService,
        ILogger<PlexSettingsProvider> logger)
    {
        _settingService = settingService;
        _logger = logger;
    }

    /// <summary>
    /// Loads the Plex URL (plain) and token (encrypted); returns null when
    /// either is missing or blank.
    /// </summary>
    public async Task<PlexSettings?> GetSettings()
    {
        var url = await _settingService.GetSetting(SettingKeys.Integration.PlexUrl);
        var token = await _settingService.GetEncryptedSetting(SettingKeys.Integration.PlexToken);

        var variables = new Dictionary<string, string?>
        {
            { "Url", url },
            { "Token", token }
        };

        var missingVariables = variables
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToList();
        if (missingVariables.Count > 0)
        {
            // An unconfigured integration is an expected state (the switch
            // ships off); the refresh is best-effort and the job reports
            // success, so a warning — not the sync path's error — is honest.
            _logger.LogWarning(
                "Plex settings are incomplete (missing: {Missing}). Skipping the library refresh.",
                string.Join(", ", missingVariables));
            return null;
        }

        return new PlexSettings(url!, token!);
    }
}
