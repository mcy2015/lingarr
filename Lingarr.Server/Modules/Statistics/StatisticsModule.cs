using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;

namespace Lingarr.Server.Modules.Statistics;

/// <summary>
/// Usage statistics.
/// </summary>
public static class StatisticsModule
{
    public static void AddStatisticsModule(this IServiceCollection services)
    {
        services.AddScoped<IStatisticsService, StatisticsService>();
    }
}
