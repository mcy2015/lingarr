using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.MySql;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Lingarr.Core;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Logging;
using Lingarr.Migrations;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Providers;
using Lingarr.Server.Services;
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
                    Url = new Uri("https://github.com/lingarr-translate/lingarr/blob/main/LICENSE")
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
        var dbConnection = DatabaseConfiguration.GetDbConnection();
        var connectionString = DatabaseConfiguration.GetConnectionString(dbConnection);
        services.AddFluentMigrator(connectionString, dbConnection);
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
        var tablePrefix = "_hangfire";
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

            var dbConnection = DatabaseConfiguration.GetDbConnection();
            var connectionString = DatabaseConfiguration.GetConnectionString(dbConnection);
            switch (dbConnection)
            {
                case "mysql":
                    ConfigureMySqlStorage(configuration, connectionString, tablePrefix);
                    break;
                case "postgres":
                case "postgresql":
                    ConfigurePostgresStorage(configuration, connectionString);
                    break;
                case "sqlite":
                    ConfigureSqLiteStorage(configuration, builder.Environment.ContentRootPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Database connection '{dbConnection}' is not supported.");
            }

            configuration.UseFilter(new JobContextFilter());
        });
    }

    /// <summary>
    /// Configures Hangfire to use MySQL storage. Connection string comes from
    /// <see cref="DatabaseConfiguration"/> — the single source of truth (the two
    /// previously diverged defaults drifted apart).
    /// </summary>
    /// <param name="configuration">Hangfire global configuration</param>
    /// <param name="connectionString">Connection string from DatabaseConfiguration</param>
    /// <param name="tablePrefix">Prefix for Hangfire tables in MySQL</param>
    private static void ConfigureMySqlStorage(IGlobalConfiguration configuration, string connectionString, string tablePrefix)
    {
        configuration.UseStorage(new MySqlStorage(connectionString, new MySqlStorageOptions
        {
            TablesPrefix = tablePrefix
        }));
    }

    /// <summary>
    /// Configures Hangfire to use PostgreSQL storage.
    /// </summary>
    /// <param name="configuration">Hangfire global configuration</param>
    /// <param name="connectionString">Connection string from DatabaseConfiguration</param>
    private static void ConfigurePostgresStorage(IGlobalConfiguration configuration, string connectionString)
    {
        configuration.UsePostgreSqlStorage(
            options => options.UseNpgsqlConnection(connectionString),
            new PostgreSqlStorageOptions
            {
                UseSlidingInvisibilityTimeout = true
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
            .UseSQLiteStorage(sqliteDbPath, new SQLiteStorageOptions
            {
                // Clean up expired jobs more often
                JobExpirationCheckInterval = TimeSpan.FromHours(1),

                // Reduce writes by increasing the aggregation counters
                CountersAggregateInterval = TimeSpan.FromMinutes(5),

                // Reduced database polling
                QueuePollInterval = TimeSpan.FromSeconds(15),

                // Job recovery timeout if worker crashes
                InvisibilityTimeout = TimeSpan.FromMinutes(jobTimeoutMinutes)
            });
    }
}
