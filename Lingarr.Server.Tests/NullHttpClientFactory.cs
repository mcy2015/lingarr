using System.Net.Http;

namespace Lingarr.Server.Tests;

/// <summary>
/// Minimal IHttpClientFactory for controller tests that construct services
/// needing an HTTP client but never exercise HTTP paths in those tests.
/// </summary>
public sealed class NullHttpClientFactory : IHttpClientFactory
{
    public static readonly NullHttpClientFactory Instance = new();

    public HttpClient CreateClient(string name) => new();
}
