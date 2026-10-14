using System;
using System.IO;
using Lingarr.Server.Modules.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lingarr.Server.Tests.Authorization;

/// <summary>
/// The auth cookie's Secure flag follows the request scheme so plain-http
/// LAN deployments (NAS at http://IP:port) keep their session — Always
/// there makes browsers silently drop the cookie and login appears
/// broken. AUTH_COOKIE_SECURE=always forces the flag for strict
/// https-only deployments. HttpOnly/SameSite hardening stays intact.
/// </summary>
public class AuthModuleCookiePolicyTests
{
    [Theory]
    [InlineData(null, CookieSecurePolicy.SameAsRequest)]
    [InlineData("always", CookieSecurePolicy.Always)]
    [InlineData("ALWAYS", CookieSecurePolicy.Always)]
    [InlineData("auto", CookieSecurePolicy.SameAsRequest)]
    public void CookieSecurePolicy_FollowsSchemeByDefaultWithAlwaysOverride(
        string? cookieSecureEnv,
        CookieSecurePolicy expected)
    {
        if (cookieSecureEnv == null)
        {
            Environment.SetEnvironmentVariable("AUTH_COOKIE_SECURE", null);
        }
        else
        {
            Environment.SetEnvironmentVariable("AUTH_COOKIE_SECURE", cookieSecureEnv);
        }
        try
        {
            var services = new ServiceCollection();
            // The host normally supplies logging; the SqliteTicketStore singleton
            // (wired into the cookie options) needs it when the provider builds.
            services.AddLogging();
            services.AddAuthModule(new StubEnvironment("Production"));

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);

            Assert.Equal(expected, options.Cookie.SecurePolicy);
            // The surrounding hardening from earlier batches stays intact.
            Assert.True(options.Cookie.HttpOnly);
            Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTH_COOKIE_SECURE", null);
        }
    }

    private sealed class StubEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Lingarr.Tests";
        public string EnvironmentName { get; set; } = environmentName;
        public string WebRootPath { get; set; } = "wwwroot";
        public string ContentRootPath { get; set; } =
            Path.Combine(Path.GetTempPath(), "lingarr-auth-module-tests");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
