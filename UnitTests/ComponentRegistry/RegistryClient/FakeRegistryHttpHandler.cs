using System.Net;

namespace UnitTests.ComponentRegistry;

/// <summary>
/// HttpMessageHandler serving canned registry documents by repo-relative path,
/// with switchable offline mode and a request counter for cache assertions.
/// </summary>
public sealed class FakeRegistryHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _documents = new();

    /// <summary>Gets the number of HTTP requests the client actually issued.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Gets or sets whether every request fails as if the machine were offline.</summary>
    public bool Offline { get; set; }

    /// <summary>Registers a document under its repo-relative path.</summary>
    public FakeRegistryHttpHandler WithDocument(string relativePath, string json)
    {
        _documents[relativePath] = json;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (Offline)
            throw new HttpRequestException("Simulated network failure.");

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        // Strip the fake host prefix "registry/" used as base URL in tests.
        var relative = path.StartsWith("registry/") ? path["registry/".Length..] : path;
        if (_documents.TryGetValue(relative, out var json))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
