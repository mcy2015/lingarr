using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.Storage.SQLite;
using Lingarr.Core;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Logging;
using Lingarr.Migrations;
using Lingarr.Server.Filters;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// Cross-cutting infrastructure: web framework, JSON, HTTP, caching, logging,
/// database, SignalR, Hangfire and the startup bootstrappers.
/// </summary>
public static class InfrastructureModule
{
    public static void AddInfrastructureModule(this WebApplicationBuilder builder)
    {
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            });

        builder.Services.AddEndpointsApiExplorer();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();

        AddSwagger(builder.Services);
        AddLogging(builder);
        AddDatabase(builder.Services);
        AddSignalR(builder.Services);
        AddHangfire(builder);

        // Cross-domain recurring-job scheduler: the jobs it registers span the
        // sync, translation, cleanup and statistics domains.
        builder.Services.AddHostedService<ScheduleInitializationService>();
        builder.Services.AddSingleton<IScheduleService, ScheduleService>();

        // Startup bootstrap: seeds the admin user and validates integration
        // and plugin settings before the app starts serving requests.
        builder.Services.AddHostedService<StartupService>();
    }

    private static void AddSwagger(IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(LingarrVersion.Number, new OpenApiInfo
            {
                Title = "Lingarr HTTP API",
                Version = LingarrVersion.Number,
                Description = "Lingarr HTTP API definition",
                License = new OpenApiLicense
                {
                    Name = "GNU Affero General Public License v3.0",
                    Url = new Uri("https://www.gnu.org/licenses/agpl-3.0.html")
                }
            });

            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
        });
    }

    private static void AddLogging(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        AddLogProviders(builder.Logging);
    }

    /// <summary>
    /// Shared with <see cref="Lingarr.Server.Modules.Translation.TranslationModule"/>, which builds a
    /// standalone logger factory for the plugin loader before the container
    /// is ready.
    /// </summary>
    internal static void AddLogProviders(ILoggingBuilder logging)
    {
        logging.AddProvider(new CustomLogFormatter(Options.Create(new CustomLogFormatterOptions())));
        // Backs the /api/logs viewer. Bounded ring buffer (1000 entries) — safe to
        // enable in Debug too, so the dev log page isn't silently empty.
        logging.AddProvider(new InMemoryLoggerProvider());
    }

    private static void AddDatabase(IServiceCollection services)
    {
        services.AddDbContext<LingarrDbContext>(options =>
        {
            DatabaseConfiguration.ConfigureDbContext(options);
        });

        // Add FluentMigrator
        services.AddFluentMigrator(DatabaseConfiguration.GetConnectionString());
    }

    private static void AddSignalR(IServiceCollection services)
    {
        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
    }

    private static void AddHangfire(WebApplicationBuilder builder)
    {
        builder.Services.AddHangfireServer(options =>
        {
            options.Queues = ["movies", "shows", "system", "translation", "webhook", "default"];
            options.WorkerCount =
                int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_JOBS"), out var maxConcurrentJobs)
                    ? maxConcurrentJobs
                    : 1;
        });

        builder.Services.AddHangfire(configuration =>
        {
            configuration
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            ConfigureSqLiteStorage(configuration, builder.Environment.ContentRootPath);

            configuration.UseFilter(new JobContextFilter());
        });
    }

    /// <summary>
    /// Configures Hangfire to use SQLite storage.
    /// </summary>
    /// <param name="configuration">Hangfire global configuration</param>
    private static void ConfigureSqLiteStorage(IGlobalConfiguration configuration, string contentRootPath)
    {
        var sqliteDbPath = Environment.GetEnvironmentVariable("DB_HANGFIRE_SQLITE_PATH")
            ?? Path.Combine(contentRootPath, "config", "Hangfire.db");
        var jobTimeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("JOB_TIMEOUT_MINUTES"), out var timeout) && timeout > 0
            ? timeout
            : 30;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqliteDbPath}"))
        {
            // add Write-Ahead Logging
            connection.Open();
            using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteScalar();

            command.CommandText = "PRAGMA busy_timeout=120000";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA synchronous=NORMAL";
            command.ExecuteNonQuery();
        }

        configuration
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            // The path-based UseSQLiteStorage hardcodes BusyTimeout=10s on
            // every pooled connection (SQLiteStorage.cs of 0.4.3). Under a
            // write storm (webhook burst + scheduler + distributed-lock
            // heartbeats) waits exceed that and every enqueue fails with
            // "database is locked" → HTTP 500 on all job-creating endpoints
            // (NAS incident 2026-10-19). The public factory constructor keeps
            // the storage's own flags but stretches the busy wait to two
            // minutes, riding out storms instead of hard-failing.
            .UseStorage(new SQLiteStorage(
                new SQLiteDbConnectionFactory(() => new SQLite.SQLiteConnection(
                    sqliteDbPath,
                    SQLite.SQLiteOpenFlags.ReadWrite | SQLite.SQLiteOpenFlags.Create | SQLite.SQLiteOpenFlags.NoMutex,
                    storeDateTimeAsTicks: true)
                {
                    BusyTimeout = TimeSpan.FromSeconds(120)
                }),
                new SQLiteStorageOptions
                {
                    // Clean up expired jobs more often
                    JobExpirationCheckInterval = TimeSpan.FromHours(1),

                    // Reduce writes by increasing the aggregation counters
                    CountersAggregateInterval = TimeSpan.FromMinutes(5),

                    // Reduced database polling
                    QueuePollInterval = TimeSpan.FromSeconds(15),

                    // Job recovery timeout if worker crashes
                    InvisibilityTimeout = TimeSpan.FromMinutes(jobTimeoutMinutes)
                }));
    }
}
