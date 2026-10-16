namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// Settings keys of the seven v1 subtitle sources (tech design ⑩). The keys
/// are declared by the <c>Manifests/*PluginManifest</c> settings forms; the
/// providers read them per call so credential changes apply without a
/// restart (SettingUpdatesHub hot reload). Every source also exposes an
/// optional <c>*_base_url</c> override — production URLs are the code
/// defaults, and pointing them at <c>scripts/dev/fake-subtitle-sources.py</c>
/// is how the E2E fake-source round runs (PRD AC25).
/// </summary>
public static class SubtitleSourceSettingKeys
{
    public static class OpenSubtitles
    {
        public const string ApiKey = "subtitle_opensubtitles_api_key";
        public const string BaseUrl = "subtitle_opensubtitles_base_url";
    }

    public static class SubSource
    {
        public const string BaseUrl = "subtitle_subsource_base_url";
    }

    public static class Subdl
    {
        public const string ApiKey = "subtitle_subdl_api_key";
        public const string BaseUrl = "subtitle_subdl_base_url";
    }

    public static class Gestdown
    {
        public const string BaseUrl = "subtitle_gestdown_base_url";
    }

    public static class Podnapisi
    {
        public const string BaseUrl = "subtitle_podnapisi_base_url";
    }

    public static class Assrt
    {
        public const string Token = "subtitle_assrt_token";
        public const string BaseUrl = "subtitle_assrt_base_url";
    }

    public static class Shooter
    {
        public const string BaseUrl = "subtitle_shooter_base_url";
    }
}
