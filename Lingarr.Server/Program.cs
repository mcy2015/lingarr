using Lingarr.Server.Extensions;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Modules.Translation;

var builder = WebApplication.CreateBuilder(args);

builder.AddInfrastructureModule();
builder.Services.AddAuthModule(builder.Environment);
builder.Services.AddSettingsModule();
builder.Services.AddIntegrationModule();
builder.Services.AddMediaModule();
builder.Services.AddSubtitleModule();
builder.Services.AddSubtitleSearchModule();
builder.Services.AddTranslationModule();
builder.Services.AddSyncModule();
builder.Services.AddStatisticsModule();

var app = builder.Build();
await app.Configure();

// Every logger factory holds the shared Serilog file logger with
// dispose:false — this stop hook is its single flush-and-close point.
app.Lifetime.ApplicationStopping.Register(SerilogFileLogger.Shutdown);

app.Run();
