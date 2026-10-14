using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class SettingController : ControllerBase
{
    private readonly ISettingService _settingService;
    private readonly ILogger<SettingController> _logger;

    public SettingController(
        ISettingService settingService,
        ILogger<SettingController> logger)
    {
        _settingService = settingService;
        _logger = logger;
    }
    
    /// <summary>
    /// Retrieves the value of a specific setting by its key.
    /// </summary>
    /// <param name="key">The key of the setting to retrieve.</param>
    /// <returns>Returns an HTTP 200 OK response with the setting value if found; otherwise,
    /// an HTTP 404 Not Found response.</returns>
    [HttpGet("{key}")]
    public async Task<ActionResult<string?>> GetSetting(string key)
    {
        var value = await _settingService.GetSetting(key);
        if (value != null)
        {
            return Ok(value);
        }

        return BadRequest("Setting not found");
    }

    /// <summary>
    /// Retrieves the values of multiple settings by their keys.
    /// </summary>
    /// <param name="keys">A list of keys for the settings to retrieve.</param>
    /// <returns>Returns an HTTP 200 OK response with a dictionary of setting keys and values.</returns>
    [HttpPost("multiple/get")]
    public async Task<ActionResult<Dictionary<string, string>>> GetSettings([FromBody] IEnumerable<string> keys)
    {
        var settings = await _settingService.GetSettings(keys);
        return Ok(settings);
    }
    
    /// <summary>
    /// Updates or creates a setting with the specified key and value.
    /// </summary>
    /// <param name="setting">The setting object containing the key and value to be updated or created.</param>
    /// <returns>Returns an HTTP 200 OK response if the setting was successfully updated or created; otherwise,
    /// an HTTP 404 Not Found response.</returns>
    [HttpPost]
    public async Task<ActionResult<bool>> SetSetting([FromBody] Setting setting)
    {
        var value = await _settingService.SetSetting(setting.Key, setting.Value);
        if (value)
        {
            // P2-5 audit line: key only, never the value (it may be a secret
            // or personal data); whether the write was plain follows from
            // which endpoint ran.
            _logger.LogInformation("Setting '{SettingKey}' updated (plain).", setting.Key);
            return Ok();
        }

        return BadRequest("Setting not found or could not be updated.");
    }

    /// <summary>
    /// Updates or creates multiple settings with the specified keys and values.
    /// </summary>
    /// <param name="settings">A dictionary where the keys are setting keys and the values are the new values to assign.</param>
    /// <returns>Returns an HTTP 200 OK response if all settings were successfully updated or created; otherwise,
    /// an HTTP 400 Bad Request response.</returns>
    [HttpPost("multiple/set")]
    public async Task<ActionResult<bool>> SetSettings([FromBody] Dictionary<string, string> settings)
    {
        var success = await _settingService.SetSettings(settings);
        if (success)
        {
            // P2-5 audit line: keys only, values are never logged.
            _logger.LogInformation(
                "Settings updated (plain): {SettingKeys}.",
                string.Join(", ", settings.Keys));
            return Ok();
        }

        return BadRequest("Some settings were not found or could not be updated.");
    }

    /// <summary>
    /// Encrypts and stores a single sensitive setting value.
    /// </summary>
    /// <param name="setting">The setting object containing the key and plaintext value to encrypt and store.</param>
    /// <returns>Returns an HTTP 200 OK response if the setting was stored; otherwise an HTTP 400 Bad Request.</returns>
    [HttpPost("encrypted")]
    public async Task<ActionResult> SetEncryptedSetting([FromBody] Setting setting)
    {
        var success = await _settingService.SetEncryptedSetting(setting.Key, setting.Value);
        if (success)
        {
            // P2-5 audit line: key + encryption flag, never the value.
            _logger.LogInformation("Setting '{SettingKey}' updated (encrypted).", setting.Key);
            return Ok();
        }

        return BadRequest("Setting could not be updated.");
    }

    /// <summary>
    /// Reports which encrypted settings are configured.
    /// </summary>
    /// <remarks>
    /// Security walk finding: this endpoint used to decrypt and return every
    /// requested secret (api_key, radarr/sonarr keys, provider secrets) as
    /// plaintext to any logged-in session. It now answers only "configured:
    /// true/false" per key — the values are decrypted server-side to test for
    /// non-emptiness but never leave the process. The API key itself is only
    /// readable through the explicit reveal endpoint
    /// (<see cref="AuthController.GetApiKey"/>), which is guarded like key
    /// generation. Provider secrets are write-only from the client's view.
    /// Reads are deliberately not audited (no new log lines).
    /// </remarks>
    /// <param name="keys">A list of setting keys to check.</param>
    /// <returns>Returns an HTTP 200 OK response with a dictionary of setting keys and configured flags.</returns>
    [HttpPost("multiple/encrypted/get")]
    public async Task<ActionResult<Dictionary<string, bool>>> GetEncryptedSettings(
        [FromBody] IEnumerable<string> keys)
    {
        var states = await _settingService.GetEncryptedSettingStates(keys);
        return Ok(states);
    }
}