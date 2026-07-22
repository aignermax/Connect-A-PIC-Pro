using System.Net;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Test double for the HTTP layer: serves canned responses per URL, counts
/// requests and can simulate a dead network. Registry tests never hit the web.
/// </summary>
public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new();

    /// <summary>Total number of requests received.</summary>
    public int RequestCount { get; private set; }

    /// <summary>When true, every request throws <see cref="HttpRequestException"/>.</summary>
    public bool SimulateNetworkFailure { get; set; }

    /// <summary>Registers a canned response body for an absolute URL.</summary>
    public void AddResponse(string url, string body) => _responses[url] = body;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (SimulateNetworkFailure)
            throw new HttpRequestException("Simulated network failure");

        var url = request.RequestUri!.ToString();
        if (!_responses.TryGetValue(url, out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
    }
}
