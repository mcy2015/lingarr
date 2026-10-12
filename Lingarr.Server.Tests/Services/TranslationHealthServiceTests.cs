using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class TranslationHealthServiceTests
{
    private static TranslationHealthService CreateService(
        out Mock<IPluginRegistry> registry,
        out Mock<ISettingService> settings,
        params IPluginManifest[] manifests)
    {
        registry = new Mock<IPluginRegistry>();
        registry.SetupGet(r => r.All).Returns(manifests
            .Select(manifest => new RegisteredPlugin { Manifest = manifest, IsBuiltIn = true })
            .ToList());
        settings = new Mock<ISettingService>();

        // the singleton tracker resolves the scoped setting service through a
        // real scope factory, so tests provide one backed by a minimal provider
        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new TranslationHealthService(registry.Object, scopeFactory);
    }

    private static IPluginManifest Manifest(
        string provider,
        string displayName,
        params PluginSettingField[] fields) =>
        new StubManifest(provider, displayName, fields);

    private sealed class StubManifest(
        string provider,
        string displayName,
        IReadOnlyList<PluginSettingField> settings)
        : IPluginManifest
    {
        public string Provider => provider;
        public string DisplayName => displayName;
        public string? Description => null;
        public bool HasRequestTemplate => false;
        public IReadOnlyList<PluginSettingField> Settings => settings;
    }

    [Fact]
    public void GetSnapshot_ReturnsNull_WhenNoTrafficRecorded()
    {
        var service = CreateService(out _, out _);

        Assert.Null(service.GetSnapshot("openai"));
    }

    [Fact]
    public void GetSnapshot_ComputesLatency_SuccessRate_AndLastError()
    {
        var service = CreateService(out _, out _);

        service.Record("openai", success: true, TimeSpan.FromMilliseconds(100));
        service.Record("openai", success: true, TimeSpan.FromMilliseconds(300));
        service.Record("openai", success: false, TimeSpan.FromMilliseconds(50), "429");

        var snapshot = service.GetSnapshot("openai");

        Assert.NotNull(snapshot);
        Assert.Equal(200, snapshot.AverageLatencyMs); // average over successful attempts only
        Assert.Equal(2.0 / 3, snapshot.SuccessRate, 5);
        Assert.Equal("429", snapshot.LastError);
        Assert.Equal(3, snapshot.SampleCount);
    }

    [Fact]
    public void Record_TrimsWindowToTwentySamples()
    {
        var service = CreateService(out _, out _);

        for (var i = 0; i < 30; i++)
        {
            service.Record("deepl", success: true, TimeSpan.FromMilliseconds(10));
        }

        var snapshot = service.GetSnapshot("deepl");

        Assert.NotNull(snapshot);
        Assert.Equal(20, snapshot.SampleCount);
        Assert.Equal(1.0, snapshot.SuccessRate, 5);
    }

    [Fact]
    public async Task GetServiceHealthAsync_MarksConfiguredFromRequiredManifestFields()
    {
        var openAiKey = new PluginSettingField
        {
            Key = "openai_api_key",
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true
        };
        var optional = new PluginSettingField
        {
            Key = "openai_extra",
            Label = "Extra",
            Type = PluginSettingType.Text,
            Required = false
        };
        var deepLKey = new PluginSettingField
        {
            Key = "deepl_api_key",
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true
        };
        var service = CreateService(
            out _,
            out var settings,
            Manifest("openai", "OpenAI", openAiKey, optional),
            Manifest("deepl", "DeepL", deepLKey));

        settings
            .Setup(s => s.GetEncryptedSetting("openai_api_key"))
            .ReturnsAsync("sk-configured");
        settings
            .Setup(s => s.GetEncryptedSetting("deepl_api_key"))
            .ReturnsAsync((string?)null);

        var health = await service.GetServiceHealthAsync();

        Assert.Equal(2, health.Count);
        var openai = health.Single(h => h.Provider == "openai");
        Assert.True(openai.Configured);
        Assert.Equal("OpenAI", openai.DisplayName);
        var deepl = health.Single(h => h.Provider == "deepl");
        Assert.False(deepl.Configured);
    }

    [Fact]
    public async Task GetOnlineServiceCountAsync_CountsConfigured_NotFailingProviders()
    {
        var required = new PluginSettingField
        {
            Key = "api_key",
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true
        };
        var service = CreateService(
            out _,
            out var settings,
            Manifest("openai", "OpenAI", required),
            Manifest("deepl", "DeepL", required),
            Manifest("gemini", "Gemini", required));

        settings
            .Setup(s => s.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync("configured");

        // no traffic: configured counts as online
        Assert.Equal(3, await service.GetOnlineServiceCountAsync());

        // a failing window demotes a provider
        for (var i = 0; i < 4; i++)
        {
            service.Record("gemini", success: false, TimeSpan.FromMilliseconds(10), "429");
        }

        Assert.Equal(2, await service.GetOnlineServiceCountAsync());
    }
}
