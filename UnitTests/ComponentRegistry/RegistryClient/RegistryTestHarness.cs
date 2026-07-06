using CAP_Core.ComponentRegistry.RegistryClient;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Shared setup for registry client tests: a stub HTTP handler pre-loaded with
/// the committed fixture files (real copies from the live registry) and a
/// throw-away cache directory that is deleted on dispose.
/// </summary>
public sealed class RegistryTestHarness : IDisposable
{
    /// <summary>Base URL the client under test is pointed at.</summary>
    public const string BaseUrl = "https://registry.test";

    /// <summary>Repo-relative path of the fixture component manifest.</summary>
    public const string ManifestPath = "processes/generic-si220/components/y-branch-1x2/component.json";

    /// <summary>Component-relative path of the fixture spectrum artifact.</summary>
    public const string SpectrumFile = "simulated/analytic-demo.json";

    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "lunima-registry-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The stub HTTP layer (request counting, canned responses, failure mode).</summary>
    public StubHttpMessageHandler Handler { get; } = new();

    /// <summary>Creates a harness whose handler serves all three fixture documents.</summary>
    public RegistryTestHarness()
    {
        Handler.AddResponse($"{BaseUrl}/index.json", ReadFixture("index.json"));
        Handler.AddResponse($"{BaseUrl}/{ManifestPath}", ReadFixture("component.json"));
        var spectrumPath = CAP_Core.ComponentRegistry.RegistryClient.RegistryClient
            .ResolveArtifactPath(ManifestPath, SpectrumFile);
        Handler.AddResponse($"{BaseUrl}/{spectrumPath}", ReadFixture("spectrum.json"));
    }

    /// <summary>Creates a client wired to the stub handler and the temp cache.</summary>
    public CAP_Core.ComponentRegistry.RegistryClient.RegistryClient CreateClient() =>
        new(new HttpClient(Handler), CreateCache(), logger: null, baseUrl: BaseUrl);

    /// <summary>Creates a cache rooted in this harness's temp directory.</summary>
    public RegistryCache CreateCache() => new(_cacheDirectory);

    /// <summary>Reads a committed fixture JSON from the test output directory.</summary>
    public static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "ComponentRegistry", "RegistryClient", "Fixtures", fileName));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }
}
