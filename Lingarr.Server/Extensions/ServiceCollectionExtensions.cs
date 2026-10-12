using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.MySql;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Settings;
using Lingarr.Core;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Logging;
using Lingarr.Server.Authorization;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Providers;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Integration;
using Lingarr.Server.Interfaces.Services.Subtitle;
using Lingarr.Server.Interfaces.Services.Sync;
using Lingarr.Server.Interfaces.Services.Translation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Listener;
using Lingarr.Server.Providers;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Integration;
using Lingarr.Server.Services.Plugins;
using Lingarr.Server.Services.Plugins.Manifests;
using Lingarr.Server.Services.Subtitle;
using Lingarr.Server.Services.Sync;
using Lingarr.Server.Services.Translation;
using Lingarr.Migrations;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Lingarr.Server.Extensions;

public static class ServiceCollectionExtensions
{
    public static void Configure(this WebApplicationBuilder builder)
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

        builder.ConfigureSwagger();
        builder.ConfigureLogging();
        builder.ConfigureDatabase();
        builder.ConfigureAuthentication();
        builder.ConfigureProviders();
        builder.ConfigureServices();
        builder.ConfigureSignalR();
        builder.ConfigureHangfire();
    }

    private static void ConfigureSwagger(this WebApplicationBuilder builder)
    {
        builder.Services.AddSwaggerGen(options =>
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

    private static void ConfigureLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        AddLogProviders(builder.Logging);
    }

    private static void AddLogProviders(ILoggingBuilder logging)
    {
        logging.AddProvider(new CustomLogFormatter(Options.Create(new CustomLogFormatterOptions())));
        // Backs the /api/logs viewer. Bounded ring buffer (1000 entries) — safe to
        // enable in Debug too, so the dev log page isn't silently empty.
        logging.AddProvider(new InMemoryLoggerProvider());
    }

    private static void ConfigureDatabase(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbContext<LingarrDbContext>(options =>
        {
            DatabaseConfiguration.ConfigureDbContext(options);
        });

        // Add FluentMigrator
        var dbConnection = DatabaseConfiguration.GetDbConnection();
        var connectionString = DatabaseConfiguration.GetConnectionString(dbConnection);
        builder.Services.AddFluentMigrator(connectionString, dbConnection);
    }

    private static void ConfigureAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "Lingarr.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        })
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("LingarrAccess", policy =>
            {
                policy.AddAuthenticationSchemes(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    ApiKeyAuthenticationHandler.SchemeName);
                policy.Requirements.Add(new LingarrAccessRequirement());
            });
        });
        builder.Services.AddSingleton<IAuthorizationHandler, LingarrAccessHandler>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, LingarrAuthorizationResponseHandler>();
    }

    private static void ConfigureProviders(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IIntegrationSettingsProvider, IntegrationSettingsProvider>();
    }

    private static void ConfigureServices(this WebApplicationBuilder builder)
    {
        // Register auth
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(
            Environment.GetEnvironmentVariable("ENCRYPTION_KEYS")?.ToLower()
            ?? Path.Combine(builder.Environment.ContentRootPath, "config", "keys")
            ));
        builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
        builder.Services.AddScoped<IAuthService, AuthService>();

        builder.Services.AddScoped<ISettingService, SettingService>();
        builder.Services.AddSingleton<ISettingChangeNotifier, SettingChangeNotifier>();
        builder.Services.AddSingleton<SettingChangedListener>();

        builder.Services.AddHostedService<ScheduleInitializationService>();
        builder.Services.AddSingleton<IScheduleService, ScheduleService>();

        builder.Services.AddScoped<IImageService, ImageService>();
        builder.Services.AddScoped<IIntegrationService, IntegrationService>();
        builder.Services.AddScoped<IMediaService, MediaService>();
        builder.Services.AddScoped<IProgressService, ProgressService>();
        builder.Services.AddScoped<IRadarrService, RadarrService>();
        builder.Services.AddScoped<ISonarrService, SonarrService>();
        builder.Services.AddScoped<ISubtitleService, SubtitleService>();
        builder.Services.AddScoped<ISubtitleImportService, SubtitleImportService>();
        builder.Services.AddSingleton<ISubtitleEncodingService, SubtitleEncodingService>();
        builder.Services.AddScoped<ISubtitleConversionService, SubtitleConversionService>();
        builder.Services.AddSingleton<IAsyncTranslationRegistry, AsyncTranslationRegistry>();
        builder.Services.AddSingleton<IJobQueue, JobQueue>();
        builder.Services.AddScoped<ITranslationRequestQueryService, TranslationRequestQueryService>();
        builder.Services.AddScoped<ITranslationRequestQueueService, TranslationRequestQueueService>();
        builder.Services.AddScoped<IProofreadRequestService, ProofreadRequestService>();
        builder.Services.AddScoped<IContentTranslationService, ContentTranslationService>();
        builder.Services.AddScoped<ITranslationRequestService, TranslationRequestService>();
        builder.Services.AddScoped<ITranslationRequestEventService, TranslationRequestEventService>();
        builder.Services.AddScoped<IMediaSubtitleProcessor, MediaSubtitleProcessor>();
        builder.Services.AddScoped<IDirectoryService, DirectoryService>();
        builder.Services.AddScoped<IMappingService, MappingService>();

        // Register subtitle services
        // Note: SrtParser/SsaParser/SrtWriter/SsaWriter are instantiated directly
        // by SubtitleService per file extension — no DI consumers exist, so no
        // (conflicting) registrations are needed.
        
        // Register translate services
        builder.Services.AddScoped<ITranslationServiceFactory, TranslationFactory>();
        builder.Services.AddScoped<Interfaces.Services.Translation.ITranslationPipeline, Lingarr.Server.Services.Translation.TranslationPipeline>();
        builder.Services.AddSingleton<LanguageCodeService>();
        builder.Services.AddSingleton<IRequestTemplateService, RequestTemplateService>();

        // Register manifests
        builder.Services.AddSingleton<IPluginManifest, AnthropicPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, OpenAiPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, GeminiPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, DeepSeekPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, MistralPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, XAiPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, LocalAiPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, DeepLPluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, LibreTranslatePluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, GoogleTranslatePluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, BingTranslatePluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, MicrosoftTranslatePluginManifest>();
        builder.Services.AddSingleton<IPluginManifest, YandexTranslatePluginManifest>();

        // Plugin discovery and the read settings
        builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();
        builder.Services.AddScoped<ISettingsAccess, SettingsAccess>();

        // Scan for external plugins before the container is ready
        var pluginLoaderLogger = LoggerFactory
            .Create(AddLogProviders)
            .CreateLogger<PluginLoader>();
        var pluginLoader = new PluginLoader(pluginLoaderLogger);
        pluginLoader.LoadPlugins(builder.Services);
        builder.Services.AddSingleton(pluginLoader);

        // Added startup service to validate new settings
        builder.Services.AddHostedService<StartupService>();

        builder.Services.AddTransient<PathConversionService>();
        builder.Services.AddScoped<IStatisticsService, StatisticsService>();
        builder.Services.AddScoped<ITelemetryService, TelemetryService>();
        builder.Services.AddScoped<ILingarrApiService, LingarrApiService>();

        // Add Sync services
        builder.Services.AddScoped<IShowSyncService, ShowSyncService>();
        builder.Services.AddScoped<IMovieSyncService, MovieSyncService>();
        builder.Services.AddScoped<IMovieSync, MovieSync>();
        builder.Services.AddScoped<IEpisodeSync, EpisodeSync>();
        builder.Services.AddScoped<ISeasonSync, SeasonSync>();
        builder.Services.AddScoped<IShowSync, ShowSync>();
        builder.Services.AddScoped<IImageSync, ImageSync>();
        
    }

    private static void ConfigureSignalR(this WebApplicationBuilder builder)
    {
        builder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
    }

    private static void ConfigureHangfire(this WebApplicationBuilder builder)
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