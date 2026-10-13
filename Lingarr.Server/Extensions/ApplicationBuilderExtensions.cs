using Hangfire;
using Lingarr.Core;
using Lingarr.Core.Configuration;
using Lingarr.Migrations;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Extensions;

public static class ApplicationBuilderExtensions
{
    public static async Task Configure(this WebApplication app)
    {
        var basePath = (Environment.GetEnvironmentVariable("BASE_PATH") ?? "").TrimEnd('/');
        if (!string.IsNullOrEmpty(basePath))
        {
            app.UsePathBase(basePath);
        }

        app.UseMiddleware<Middleware.GlobalExceptionMiddleware>();

        app.UseRouting();

        app.MapHubs();
        await app.ApplyMigrations();
        await app.MigrateApiKeyEncryption();

        if (app.Environment.IsDevelopment())
        {
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = []
            });
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint($"/swagger/{LingarrVersion.Number}/swagger.json",
                    $"Lingarr HTTP API {LingarrVersion.Number}");
                options.EnableTryItOutByDefault();
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.UseStaticFiles();
        app.ConfigureSpa(basePath);
    }

    private static void MapHubs(this WebApplication app)
    {
        app.MapHub<TranslationRequestsHub>("/signalr/TranslationRequests")
            .RequireAuthorization("LingarrAccess");
        app.MapHub<SettingUpdatesHub>("/signalr/SettingUpdates")
            .RequireAuthorization("LingarrAccess");
        app.MapHub<JobProgressHub>("/signalr/JobProgress")
            .RequireAuthorization("LingarrAccess");
    }

    private static Task ApplyMigrations(this WebApplication app)
    {
        try
        {
            app.Logger.LogInformation("Applying migrations...");
            MigrationConfiguration.RunMigrations(app.Services);
            app.Logger.LogInformation("Migrations applied successfully.");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "An error occurred while applying migrations.");
            throw;
        }

        return Task.CompletedTask;
    }

    private static async Task MigrateApiKeyEncryption(this WebApplication app)
    {
        string[] apiKeys =
        [
            SettingKeys.Authentication.ApiKey,
            SettingKeys.Integration.RadarrApiKey,
            SettingKeys.Integration.SonarrApiKey,
            SettingKeys.Translation.OpenAi.ApiKey,
            SettingKeys.Translation.Anthropic.ApiKey,
            SettingKeys.Translation.Gemini.ApiKey,
            SettingKeys.Translation.DeepSeek.ApiKey,
            SettingKeys.Translation.Mistral.ApiKey,
            SettingKeys.Translation.XAi.ApiKey,
            SettingKeys.Translation.DeepL.DeeplApiKey,
            SettingKeys.Translation.LibreTranslate.ApiKey,
            SettingKeys.Translation.LocalAi.ApiKey,
        ];

        using var scope = app.Services.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        foreach (var key in apiKeys)
        {
            var value = await settingService.GetSetting(key);
            if (string.IsNullOrEmpty(value)) continue;

            try
            {
                encryptionService.Decrypt(value);
            }
            catch
            {
                await settingService.SetEncryptedSetting(key, value);
                app.Logger.LogInformation("Migrated '{Key}' to encrypted storage.", key);
            }
        }
    }

    private static void ConfigureSpa(this WebApplication app, string basePath)
    {
        app.MapWhen(httpContext =>
                httpContext.Request.Path.Value != null &&
                !httpContext.Request.Path.Value.StartsWith("/api") &&
                !httpContext.Request.Path.Value.StartsWith("/signalr"),
            configBuilder =>
            {
                var index = File.ReadAllText(Path.Combine(app.Environment.WebRootPath, "index.html"));
                index = index.Replace("<head>",
                    $"<head><base href=\"{basePath}/\">" +
                    $"<meta name=\"lingarr-timezone\" content=\"{TimeZoneConfiguration.IanaId}\">");

                configBuilder.Run(async ctx =>
                {
                    ctx.Response.ContentType = "text/html";
                    await ctx.Response.WriteAsync(index);
                });
            });
    }
}