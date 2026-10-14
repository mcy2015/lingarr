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
/// P2-1: the auth cookie must be marked Secure in every non-Development
/// environment. SameAsRequest there would let the session cookie travel over
/// plain http; Development keeps it so plain-http dev logins still work.
/// </summary>
public class AuthModuleCookiePolicyTests
{
    [Theory]
    [InlineData("Production", CookieSecurePolicy.Always)]
    [InlineData("Staging", CookieSecurePolicy.Always)]
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    public void CookieSecurePolicy_FollowsTheEnvironment(
        string environmentName,
        CookieSecurePolicy expected)
    {
        var services = new ServiceCollection();
        // The host normally supplies logging; the SqliteTicketStore singleton
        // (wired into the cookie options) needs it when the provider builds.
        services.AddLogging();
        services.AddAuthModule(new StubEnvironment(environmentName));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(expected, options.Cookie.SecurePolicy);
        // The surrounding hardening from earlier batches stays intact.
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
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
