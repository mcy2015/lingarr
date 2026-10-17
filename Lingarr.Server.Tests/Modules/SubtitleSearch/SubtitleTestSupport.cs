using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Settings;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Serializes the test classes that touch <c>SubtitleHttp</c>'s
/// process-wide request-throttle state (the OpenSubtitles/assrt pacing
/// cases measure real waits, and any concurrent
/// <c>ClearRequestThrottling</c>/<c>SetMinimumRequestInterval</c> from a
/// parallel class would zero those waits and flake the assertions).
/// Members: the provider test classes with pacing/reset logic and the
/// download-pipeline class that lifts the assrt interval.
/// </summary>
[CollectionDefinition("SubtitleHttpThrottle", DisableParallelization = true)]
public sealed class SubtitleHttpThrottleCollection
{
}

/// <summary>
/// Fixture access for the subtitle-source tests: responses live under
/// <c>Lingarr.Server.Tests/Fixtures/Subtitles/{provider}/</c> on disk (JSON,
/// each folder with a manifest.json recording note) and
/// are located by walking up from the assembly directory to the solution
/// root — no csproj copy wiring, same layout in dev and CI checkouts.
/// </summary>
internal static class SubtitleFixtures
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    internal static string Read(string provider, string fileName)
    {
        var path = Path.Combine(
            RepoRoot.Value, "Lingarr.Server.Tests", "Fixtures", "Subtitles", provider, fileName);
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lingarr.slnx")))
        {
            directory = directory.Parent!;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repository root (Lingarr.slnx) above the test assembly.");
    }
}

/// <summary>
/// Scriptable <see cref="HttpMessageHandler"/> for provider tests: enqueue
/// one response per expected request (search, download…); requests are
/// recorded for URL/header assertions. A queued callback returning null
/// leaves the handler waiting — used with a short HttpClient timeout to
/// exercise the Timeout failure bucket without real wall-clock delays.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage?>> _responses = new();

    internal List<HttpRequestMessage> Requests { get; } = new();

    internal StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage?> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    internal StubHttpMessageHandler Enqueue(HttpResponseMessage response)
        => Enqueue(_ => response);

    internal StubHttpMessageHandler EnqueueJson(
        string json, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    internal StubHttpMessageHandler EnqueueXml(
        string xml, HttpStatusCode status = HttpStatusCode.OK)
        => Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
        });

    internal StubHttpMessageHandler EnqueueStatus(
        HttpStatusCode status, string? retryAfter = null)
        => Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is not null)
            {
                response.Headers.Add("Retry-After", retryAfter);
            }

            return response;
        });

    /// <summary>
    /// Queues a "hanging" request: the response task never completes, so a
    /// client with a short Timeout exercises the Timeout failure bucket
    /// without real wall-clock delays or blocked threads.
    /// </summary>
    internal StubHttpMessageHandler EnqueueHang()
        => Enqueue(_ => null);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Unexpected HTTP request to {request.RequestUri}: no queued response.");
        }

        var response = _responses.Dequeue()(request);
        if (response is not null)
        {
            return Task.FromResult(response);
        }

        // Hanging responder (EnqueueHang): complete exactly when the caller
        // cancels — the HttpClient timeout CTS trips in milliseconds and the
        // await unwinds immediately instead of relying on HttpClient's own
        // timeout wrapper.
        var tcs = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    internal HttpClient ToClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("http://subtitle.test/") };
        if (timeout is { } span)
        {
            client.Timeout = span;
        }

        return client;
    }

    internal Uri? SingleRequestUri => Requests.Single().RequestUri;

    internal HttpRequestMessage SingleRequest => Requests.Single();
}

/// <summary>
/// <see cref="ISettingsAccess"/> doubles: unconfigured by default (nulls),
/// with helpers for the credential and base-URL keys the subtitle sources
/// read.
/// </summary>
internal static class SubtitleSettingsDouble
{
    internal static Mock<ISettingsAccess> Create(
        IDictionary<string, string>? settings = null,
        IDictionary<string, string>? secrets = null)
    {
        var mock = new Mock<ISettingsAccess>();
        mock
            .Setup(access => access.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => settings?.TryGetValue(key, out var value) == true ? value : null);
        mock
            .Setup(access => access.GetEncryptedSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => secrets?.TryGetValue(key, out var value) == true ? value : null);
        return mock;
    }
}
