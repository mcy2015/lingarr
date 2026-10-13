
namespace Lingarr.Server.Modules.Settings;

/// <summary>
/// Singleton hub for setting-change notifications. Scoped <see cref="SettingService"/>
/// instances publish through it instead of wiring per-scope event subscriptions
/// to the singleton listener in their constructors.
/// </summary>
public interface ISettingChangeNotifier
{
    event SettingChangedHandler? Changed;

    void Notify(SettingService source, string setting);
}

public sealed class SettingChangeNotifier : ISettingChangeNotifier
{
    public event SettingChangedHandler? Changed;

    public void Notify(SettingService source, string setting)
    {
        Changed?.Invoke(source, setting);
    }
}
