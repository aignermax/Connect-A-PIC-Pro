using System.Net;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Test double for the HTTP layer: serves canned responses per URL, counts
/// requests and can simulate a dead network. Registry tests never hit the web.
/// </summary>
public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new();
    private readonly Dictionary<string, TaskCompletionSource> _holds = new();
    private int _afterRequestCount;
    private Action? _afterRequestsAction;

    /// <summary>Total number of requests received.</summary>
    public int RequestCount { get; private set; }

    /// <summary>When true, every request throws <see cref="HttpRequestException"/>.</summary>
    public bool SimulateNetworkFailure { get; set; }

    /// <summary>Registers a canned response body for an absolute URL.</summary>
    public void AddResponse(string url, string body) => _responses[url] = body;

    /// <summary>
    /// Runs <paramref name="action"/> once, after <paramref name="requestCount"/>
    /// further requests were served — e.g. to kill the network mid-flow.
    /// </summary>
    public void AfterRequests(int requestCount, Action action)
    {
        _afterRequestCount = RequestCount + requestCount;
        _afterRequestsAction = action;
    }

    /// <summary>Parks responses for <paramref name="url"/> until <see cref="Release"/> is called.</summary>
    public void Hold(string url) =>
        _holds[url] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets previously held responses for <paramref name="url"/> complete.</summary>
    public void Release(string url)
    {
        if (_holds.Remove(url, out var gate))
            gate.SetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (SimulateNetworkFailure)
            throw new HttpRequestException("Simulated network failure");

        var url = request.RequestUri!.ToString();
        if (_holds.TryGetValue(url, out var gate))
            await gate.Task;
        var response = _responses.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        if (_afterRequestsAction is not null && RequestCount >= _afterRequestCount)
        {
            var action = _afterRequestsAction;
            _afterRequestsAction = null;
            action();
        }
        return response;
    }
}
