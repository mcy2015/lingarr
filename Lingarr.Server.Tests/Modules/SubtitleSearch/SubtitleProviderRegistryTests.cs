using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Registry wiring (tech design ①/②): the registry reads name and
/// requiresAuth from <see cref="SubtitleProviderAttribute"/>, falls back to
/// the Provider property when the attribute is missing, keeps the first
/// registration on duplicate keys (PluginRegistry semantics), links
/// <see cref="ISubtitleSourceManifest"/> instances by provider key and
/// resolves keys case-insensitively.
/// </summary>
public class SubtitleProviderRegistryTests
{
    [SubtitleProvider("fakesubs", requiresAuth: true)]
    private sealed class AuthedFakeProvider : FakeProvider
    {
        public override string Provider => "fakesubs";
    }

    [SubtitleProvider("keyless")]
    private sealed class NoAuthFakeProvider : FakeProvider
    {
        public override string Provider => "keyless";
    }

    private sealed class AttributelessFakeProvider : FakeProvider
    {
        public override string Provider => "attributeless";
    }

    [SubtitleProvider("fakesubs")]
    private sealed class DuplicateFakeProvider : FakeProvider
    {
        public override string Provider => "fakesubs";
    }

    private abstract class FakeProvider : ISubtitleProvider
    {
        public abstract string Provider { get; }

        public Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("Test double; M2 fills real downloads.");
    }

    private sealed class FakeSourceManifest(string provider) : ISubtitleSourceManifest
    {
        public string Provider { get; } = provider;
        public string DisplayName { get; } = $"Fake {provider}";
        public string? Description { get; } = null;
        public IReadOnlyList<PluginSettingField> Settings { get; } = [];
    }

    private static SubtitleProviderRegistry CreateRegistry(
        ISubtitleProvider[] providers,
        ISubtitleSourceManifest[] manifests) => new(
        providers,
        manifests,
        NullLogger<SubtitleProviderRegistry>.Instance);

    [Fact]
    public void Attribute_SuppliesNameAndRequiresAuth()
    {
        var provider = new AuthedFakeProvider();
        var registry = CreateRegistry([provider], []);

        var entry = Assert.Single(registry.All);
        Assert.Same(provider, entry.Provider);
        Assert.True(entry.RequiresAuth);
    }

    [Fact]
    public void MissingAttribute_FallsBackToProviderPropertyAndNoAuth()
    {
        var registry = CreateRegistry([new AttributelessFakeProvider()], []);

        var entry = Assert.Single(registry.All);
        Assert.False(entry.RequiresAuth);
        Assert.NotNull(registry.Find("attributeless"));
    }

    [Fact]
    public void DuplicateProviderKey_FirstRegistrationWins()
    {
        var first = new AuthedFakeProvider();
        var registry = CreateRegistry([first, new DuplicateFakeProvider()], []);

        var entry = Assert.Single(registry.All);
        Assert.Same(first, entry.Provider);
        // requiresAuth comes from the surviving registration's attribute.
        Assert.True(entry.RequiresAuth);
    }

    [Fact]
    public void Manifest_IsLinkedByProviderKey()
    {
        var manifest = new FakeSourceManifest("fakesubs");
        var registry = CreateRegistry(
            [new AuthedFakeProvider(), new NoAuthFakeProvider()],
            [manifest]);

        var linked = registry.Find("fakesubs");
        Assert.NotNull(linked);
        Assert.Same(manifest, linked.Manifest);

        // No manifest registered for this source: null, not an error.
        var unlinked = registry.Find("keyless");
        Assert.NotNull(unlinked);
        Assert.Null(unlinked.Manifest);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndUnknownReturnsNull()
    {
        var registry = CreateRegistry([new AuthedFakeProvider()], []);

        Assert.NotNull(registry.Find("FakeSubs"));
        Assert.Null(registry.Find("doesnotexist"));
    }

    [Fact]
    public void RegistrationOrder_IsPreserved()
    {
        var registry = CreateRegistry(
            [new AuthedFakeProvider(), new NoAuthFakeProvider(), new AttributelessFakeProvider()],
            []);

        Assert.Equal(["fakesubs", "keyless", "attributeless"],
            registry.All.Select(entry => entry.Provider.Provider).ToArray());
    }

    [Fact]
    public void AllSevenProductionSources_DeclareAttributeNameMatchingProviderKey()
    {
        // The DI wiring (SubtitleSearchModule.AddSource) resolves the named
        // HttpClient from the attribute name — attribute/provider mismatches
        // would silently search under the wrong registry key.
        var providerTypes = new[]
        {
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.OpenSubtitlesProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.SubSourceProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.SubdlProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.GestdownProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.XunleiProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.AssrtProvider),
            typeof(Lingarr.Server.Modules.SubtitleSearch.Providers.ShooterProvider)
        };

        // Attribute contract first — no instances needed. The names are the
        // DI HttpClient names and the registry keys; a typo here would
        // silently orphan a source.
        var expectedNames = new[]
        {
            "opensubtitles", "subsource", "subdl", "gestdown", "xunlei", "assrt", "shooter"
        };
        for (var i = 0; i < providerTypes.Length; i++)
        {
            var attribute = (SubtitleProviderAttribute?)Attribute.GetCustomAttribute(
                providerTypes[i], typeof(SubtitleProviderAttribute));
            Assert.NotNull(attribute);
            Assert.Equal(expectedNames[i], attribute!.Name);
        }

        // Then the full registry: seven sources in the module registration
        // order (xunlei slotted after gestdown before assrt, batch R2), each
        // linked to its manifest.
        var cache = new SubtitleCandidateCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullLogger<SubtitleCandidateCache>.Instance);
        var settings = SubtitleSettingsDouble.Create().Object;
        var httpClient = new System.Net.Http.HttpClient();
        // Concrete ctor signature is (ISettingsAccess, SubtitleCandidateCache,
        // [SubtitleQuotaStore — OpenSubtitles only], HttpClient, ILogger<T>) —
        // the cache parameter is the concrete type, so reflection-by-object[]
        // cannot match it. Construct explicitly.
        var quotaStore = new Lingarr.Server.Modules.SubtitleSearch.Orchestration.SubtitleQuotaStore();
        var providers = new ISubtitleProvider[]
        {
            new Lingarr.Server.Modules.SubtitleSearch.Providers.OpenSubtitlesProvider(settings, cache, quotaStore, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.OpenSubtitlesProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.SubSourceProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.SubSourceProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.SubdlProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.SubdlProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.GestdownProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.GestdownProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.XunleiProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.XunleiProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.AssrtProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.AssrtProvider>.Instance),
            new Lingarr.Server.Modules.SubtitleSearch.Providers.ShooterProvider(settings, cache, httpClient, NullLogger<Lingarr.Server.Modules.SubtitleSearch.Providers.ShooterProvider>.Instance)
        };
        var manifests = new ISubtitleSourceManifest[]
        {
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.OpenSubtitlesPluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.SubSourcePluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.SubdlPluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.GestdownPluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.XunleiPluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.AssrtPluginManifest(),
            new Lingarr.Server.Modules.SubtitleSearch.Manifests.ShooterPluginManifest()
        };

        var registry = CreateRegistry(providers.ToArray(), manifests);

        Assert.Equal(
            ["opensubtitles", "subsource", "subdl", "gestdown", "xunlei", "assrt", "shooter"],
            registry.All.Select(entry => entry.Provider.Provider).ToArray());
        Assert.All(registry.All, entry => Assert.NotNull(entry.Manifest));
        Assert.All(registry.All, entry => Assert.Equal(entry.Provider.Provider, entry.Manifest!.Provider));
        // requiresAuth: OpenSubtitles, assrt and subsource declare
        // credentials (subsource since the R4 v1 migration made its API key
        // mandatory); the other four are keyless (⑩ + R2's xunlei + R4).
        Assert.True(registry.Find("opensubtitles")!.RequiresAuth);
        Assert.True(registry.Find("assrt")!.RequiresAuth);
        Assert.True(registry.Find("subsource")!.RequiresAuth);
        Assert.False(registry.Find("subdl")!.RequiresAuth);
        Assert.False(registry.Find("gestdown")!.RequiresAuth);
        Assert.False(registry.Find("xunlei")!.RequiresAuth);
        Assert.False(registry.Find("shooter")!.RequiresAuth);
    }

    /// <summary>NullLogger&lt;T&gt;.Instance for a provider type resolved at runtime.</summary>
}
