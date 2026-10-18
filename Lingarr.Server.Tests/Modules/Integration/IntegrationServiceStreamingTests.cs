using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Integration;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Integration;

/// <summary>
/// Tests for the streaming JSON array deserialization used by the full-library
/// Radarr/Sonarr pulls. A 100k-movie library is a 300-500MB payload; these tests
/// pin the contract that elements are yielded incrementally, in order, and that
/// the response stream is released on completion, early exit and failure.
/// </summary>
public class IntegrationServiceStreamingTests
{
    private const int LargeItemCount = 20_000;

    private static readonly IntegrationSettingKeys RadarrKeys = new()
    {
        Url = "radarr_url",
        ApiKey = "radarr_api_key"
    };

    private static IntegrationService CreateService(
        HttpMessageHandler handler,
        out Mock<IIntegrationSettingsProvider> settingsProvider)
    {
        settingsProvider = new Mock<IIntegrationSettingsProvider>();
        settingsProvider
            .Setup(p => p.GetSettings(It.IsAny<IntegrationSettingKeys>()))
            .ReturnsAsync(new IntegrationSettings { Url = "http://radarr.local", ApiKey = "key" });
        return new IntegrationService(new HttpClient(handler), settingsProvider.Object);
    }

    private static string SerializeMovie(int id) => JsonSerializer.Serialize(new RadarrMovie
    {
        Id = id,
        Title = $"Movie {id}",
        Path = $"/movies/Movie {id}",
        RootFolderPath = "/movies",
        Added = "2026-01-01T00:00:00Z",
        HasFile = true,
        MovieFile = new RadarrMovieFile { Path = $"/movies/Movie {id}/Movie {id}.mkv" },
        Images = new List<IntegrationImage>()
    });

    private static byte[] BuildLargeJsonArray()
    {
        var builder = new StringBuilder(LargeItemCount * 140);
        builder.Append('[');
        for (var i = 0; i < LargeItemCount; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(SerializeMovie(i));
        }
        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task StreamApiResponse_YieldsLargeArrayInOrder()
    {
        // Arrange
        var payload = BuildLargeJsonArray();
        var service = CreateService(new FixedResponseHandler(payload), out _);

        var ids = new List<int>(LargeItemCount);

        // Act
        await foreach (var movie in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
        {
            ids.Add(movie.Id);
            Assert.Equal($"Movie {movie.Id}", movie.Title);
        }

        // Assert
        Assert.Equal(LargeItemCount, ids.Count);
        Assert.Equal(Enumerable.Range(0, LargeItemCount), ids);
    }

    [Fact]
    public async Task StreamApiResponse_DisposesResponseStreamAfterFullEnumeration()
    {
        var payload = BuildLargeJsonArray();
        var trackingStream = new TrackingStream(new MemoryStream(payload));
        var service = CreateService(new FixedResponseHandler(trackingStream), out _);

        var count = 0;
        await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
        {
            count++;
        }

        Assert.Equal(LargeItemCount, count);
        Assert.True(trackingStream.Disposed);
    }

    [Fact]
    public async Task StreamApiResponse_EarlyBreakDisposesResponseStream()
    {
        var payload = BuildLargeJsonArray();
        var trackingStream = new TrackingStream(new MemoryStream(payload));
        var service = CreateService(new FixedResponseHandler(trackingStream), out _);

        var taken = 0;
        await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
        {
            taken++;
            if (taken == 5)
            {
                break;
            }
        }

        Assert.Equal(5, taken);
        Assert.True(trackingStream.Disposed);
    }

    [Fact]
    public async Task StreamApiResponse_MidStreamFailure_PropagatesAndDisposesStream()
    {
        // The body dies halfway through the array (connection reset): whatever was
        // committed inside complete batches stays, but the failure must surface to
        // the consumer and the socket must be released.
        var payload = BuildLargeJsonArray();
        var throwingStream = new ThrowingAfterStream(payload, payload.Length / 2);
        var service = CreateService(new FixedResponseHandler(throwingStream), out _);

        var received = 0;
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
            {
                received++;
            }
        });

        Assert.NotEqual(LargeItemCount, received);
        Assert.True(throwingStream.Disposed, "response stream must be disposed after a mid-stream failure");
    }

    [Fact]
    public async Task StreamApiResponse_NonSuccessStatusCode_ThrowsHttpRequestException()
    {
        var service = CreateService(new FixedResponseHandler(
            Encoding.UTF8.GetBytes("boom"), HttpStatusCode.InternalServerError), out _);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
            {
            }
        });
    }

    [Fact]
    public async Task StreamApiResponse_MissingSettings_ThrowsInsteadOfYieldingEmpty()
    {
        // An empty stream must never be producible from "settings not configured":
        // consumers treat empty as "the upstream library is empty" and would wipe
        // orphaned rows.
        var settingsProvider = new Mock<IIntegrationSettingsProvider>();
        settingsProvider
            .Setup(p => p.GetSettings(It.IsAny<IntegrationSettingKeys>()))
            .ReturnsAsync((IntegrationSettings?)null);
        var service = new IntegrationService(new HttpClient(new FixedResponseHandler(Array.Empty<byte>())), settingsProvider.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
            {
            }
        });
    }

    [Fact]
    public async Task StreamApiResponse_EmptyArray_YieldsNothing()
    {
        var service = CreateService(
            new FixedResponseHandler(Encoding.UTF8.GetBytes("[]")), out _);

        var count = 0;
        await foreach (var _ in service.StreamApiResponse<RadarrMovie>("/api/v3/movie/", RadarrKeys))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    // ── failure status codes must ride on the exception ────────────────

    /// <summary>
    /// Integration request failures carry the HTTP status code so callers'
    /// <c>catch … when (ex.StatusCode == NotFound)</c> guards can fire — a
    /// message-only construction left every 404 (a deleted movie/episode in
    /// *arr, a benign reconcile-later case) logged as a hard error.
    /// </summary>
    [Fact]
    public async Task FailedResponse_ThrowsWithTheStatusCode()
    {
        var handler = new FixedResponseHandler(
            "not found".Select(c => (byte)c).ToArray(), HttpStatusCode.NotFound);
        var service = CreateService(handler, out _);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetApiResponse<List<SonarrEpisode>>("/api/v3/episode/123", RadarrKeys));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly Func<Stream> _streamFactory;
        private readonly HttpStatusCode _statusCode;

        public FixedResponseHandler(byte[] payload, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this(() => new MemoryStream(payload), statusCode)
        {
        }

        public FixedResponseHandler(Stream stream, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this(() => stream, statusCode)
        {
        }

        private FixedResponseHandler(Func<Stream> streamFactory, HttpStatusCode statusCode)
        {
            _streamFactory = streamFactory;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StreamContent(_streamFactory())
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>A pass-through stream that records disposal.</summary>
    private sealed class TrackingStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            inner.Dispose();
        }
    }

    /// <summary>
    /// Serves the first <c>throwAfter</c> bytes of the payload, then fails with an
    /// IOException on every subsequent read — a connection reset mid-body.
    /// </summary>
    private sealed class ThrowingAfterStream(byte[] payload, int throwAfter) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= throwAfter)
            {
                throw new IOException("Simulated connection reset mid-stream");
            }

            var toCopy = Math.Min(count, throwAfter - _position);
            toCopy = Math.Min(toCopy, payload.Length - _position);
            Array.Copy(payload, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
