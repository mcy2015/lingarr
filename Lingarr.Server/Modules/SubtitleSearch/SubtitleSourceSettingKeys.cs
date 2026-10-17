namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// Settings keys of the v1 subtitle sources (tech design ⑩ — seven since
/// the 2026-10 xunlei addition, which backfilled the dead-site removal).
/// The keys
/// are declared by the <c>Manifests/*PluginManifest</c> settings forms; the
/// providers read them per call so credential changes apply without a
/// restart (SettingUpdatesHub hot reload). Every source also exposes an
/// optional <c>*_base_url</c> override — production URLs are the code
/// defaults, and pointing them at <c>scripts/dev/fake-subtitle-sources.py</c>
/// is how the E2E fake-source round runs (PRD AC25).
/// <para>
/// Design-⑧ polish batch additions: per-source enable switches
/// (<c>subtitle_{source}_enabled</c>, default <i>enabled</i> — a missing key
/// never disables a source, so no migration is needed) and the optional
/// proxy overrides (<see cref="GlobalProxyUrl"/> plus per-source
/// <c>subtitle_{source}_proxy_url</c>, applied when the module builds the
/// source's HttpClient handlers — proxy changes therefore need a restart).
/// </para>
/// </summary>
public static class SubtitleSourceSettingKeys
{
    /// <summary>
    /// Optional global HTTP proxy for every subtitle-source client
    /// (<c>http://host:port</c>). A per-source
    /// <c>subtitle_{source}_proxy_url</c> overrides it for that source.
    /// Read once per handler lifetime when the module wires the named
    /// HttpClients — changing it requires a restart.
    /// </summary>
    public const string GlobalProxyUrl = "subtitle_proxy_url";

    /// <summary>Settings key of one source's enable switch.</summary>
    public static string EnabledKey(string source)
        => $"subtitle_{source.ToLowerInvariant()}_enabled";

    /// <summary>Settings key of one source's proxy override.</summary>
    public static string ProxyUrlKey(string source)
        => $"subtitle_{source.ToLowerInvariant()}_proxy_url";

    /// <summary>
    /// Whether a stored enable-switch value means "enabled". Absent, blank
    /// or unrecognized values default to enabled — only an explicit
    /// <c>"false"</c> disables a source (rollout-safe without a migration).
    /// </summary>
    public static bool IsEnabled(string? storedValue)
        => !(storedValue is not null && storedValue.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

    public static class OpenSubtitles
    {
        public const string ApiKey = "subtitle_opensubtitles_api_key";
        public const string BaseUrl = "subtitle_opensubtitles_base_url";
        public const string ProxyUrl = "subtitle_opensubtitles_proxy_url";
    }

    public static class SubSource
    {
        public const string BaseUrl = "subtitle_subsource_base_url";
        public const string ProxyUrl = "subtitle_subsource_proxy_url";
    }

    public static class Subdl
    {
        public const string ApiKey = "subtitle_subdl_api_key";
        public const string BaseUrl = "subtitle_subdl_base_url";
        public const string ProxyUrl = "subtitle_subdl_proxy_url";
    }

    public static class Gestdown
    {
        public const string BaseUrl = "subtitle_gestdown_base_url";
        public const string ProxyUrl = "subtitle_gestdown_proxy_url";
    }

    public static class Assrt
    {
        public const string Token = "subtitle_assrt_token";
        public const string BaseUrl = "subtitle_assrt_base_url";
        public const string ProxyUrl = "subtitle_assrt_proxy_url";
    }

    public static class Shooter
    {
        public const string BaseUrl = "subtitle_shooter_base_url";
        public const string ProxyUrl = "subtitle_shooter_proxy_url";
    }

    /// <summary>
    /// Xunlei keys (batch R2, 2026-10): no secret — the endpoint is
    /// unauthenticated; the base URL defaults to the code-side production
    /// value and is overridable for the fake-source E2E round.
    /// </summary>
    public static class Xunlei
    {
        public const string BaseUrl = "subtitle_xunlei_base_url";
        public const string ProxyUrl = "subtitle_xunlei_proxy_url";
    }
}
