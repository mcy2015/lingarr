namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// Service for communicating with the Lingarr API (api.lingarr.com)
/// </summary>
public interface ILingarrApiService
{
    /// <summary>
    /// Gets the latest recommended version from Lingarr API
    /// </summary>
    /// <returns>Version string or null if unavailable</returns>
    Task<string?> GetLatestVersion();
}
