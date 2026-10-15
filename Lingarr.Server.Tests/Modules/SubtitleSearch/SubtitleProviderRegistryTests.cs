using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// M1 registry wiring (tech design ①/②): the registry reads name and
/// requiresAuth from <see cref="SubtitleProviderAttribute"/>, falls back to
/// the Provider property when the attribute is missing, keeps the first
/// registration on duplicate keys (PluginRegistry semantics), links
/// IPluginManifest instances by provider key for the M2 settings forms and
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

    private sealed class FakeManifest(string provider) : IPluginManifest
    {
        public string Provider { get; } = provider;
        public string DisplayName { get; } = $"Fake {provider}";
        public string? Description { get; } = null;
        public IReadOnlyList<PluginSettingField> Settings { get; } = [];
    }

    private static SubtitleProviderRegistry CreateRegistry(
        ISubtitleProvider[] providers,
        IPluginManifest[] manifests) => new(
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
        var manifest = new FakeManifest("fakesubs");
        var other = new FakeManifest("notasource");
        var registry = CreateRegistry(
            [new AuthedFakeProvider(), new NoAuthFakeProvider()],
            [manifest, other]);

        var linked = registry.Find("fakesubs");
        Assert.NotNull(linked);
        Assert.Same(manifest, linked.Manifest);

        // A manifest without a matching provider (today: the translation
        // plugin manifests) is simply not linked, not an error.
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
}
