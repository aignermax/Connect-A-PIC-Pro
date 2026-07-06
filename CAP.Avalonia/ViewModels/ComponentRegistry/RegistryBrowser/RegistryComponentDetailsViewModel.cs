using System.Collections.ObjectModel;
using System.Globalization;
using CAP_Core.ComponentRegistry.RegistryClient;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// Detail pane of the registry browser: description, ports, design parameters
/// and per-artifact provenance of the currently selected registry component.
/// </summary>
public partial class RegistryComponentDetailsViewModel : ObservableObject
{
    /// <summary>True while the component manifest is being fetched.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Non-empty when the manifest could not be loaded.</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>Component description from the manifest.</summary>
    [ObservableProperty]
    private string _description = "";

    /// <summary>Summary of the optical ports, e.g. <c>3 optical ports: o1, o2, o3</c>.</summary>
    [ObservableProperty]
    private string _portsText = "";

    /// <summary>License identifier of the component data.</summary>
    [ObservableProperty]
    private string _license = "";

    /// <summary>True when the manifest declares design parameters.</summary>
    [ObservableProperty]
    private bool _hasParameters;

    /// <summary>True when the manifest declares any artifacts.</summary>
    [ObservableProperty]
    private bool _hasArtifacts;

    /// <summary>Design parameters formatted as <c>name = value</c> lines.</summary>
    public ObservableCollection<string> Parameters { get; } = new();

    /// <summary>Artifacts of the component with status and provenance.</summary>
    public ObservableCollection<RegistryArtifactDisplay> Artifacts { get; } = new();

    /// <summary>
    /// Loads the manifest at <paramref name="manifestPath"/> and populates the pane.
    /// Never throws — failures surface via <see cref="ErrorMessage"/>.
    /// </summary>
    public async Task LoadAsync(RegistryClient client, string manifestPath)
    {
        IsLoading = true;
        Clear();
        var result = await client.GetComponentAsync(manifestPath);
        if (result.IsSuccess)
            Populate(result.Value!);
        else
            ErrorMessage = $"Could not load component details: {result.ErrorMessage}";
        IsLoading = false;
    }

    /// <summary>Resets the pane to its empty state (no component selected).</summary>
    public void Clear()
    {
        ErrorMessage = "";
        Description = "";
        PortsText = "";
        License = "";
        Parameters.Clear();
        Artifacts.Clear();
        HasParameters = false;
        HasArtifacts = false;
    }

    private void Populate(ComponentManifest manifest)
    {
        Description = manifest.Description;
        PortsText = manifest.Ports.Count == 0
            ? ""
            : string.Format(CultureInfo.InvariantCulture, "{0} optical ports: {1}",
                manifest.Ports.Count, string.Join(", ", manifest.Ports.Select(p => p.Name)));
        License = manifest.License;

        foreach (var (key, value) in manifest.Parameters)
            Parameters.Add($"{key} = {value}");

        foreach (var artifact in manifest.Artifacts.Simulated)
            Artifacts.Add(RegistryArtifactDisplay.From("simulated", artifact));
        foreach (var artifact in manifest.Artifacts.Measured)
            Artifacts.Add(RegistryArtifactDisplay.From("measured", artifact));

        HasParameters = Parameters.Count > 0;
        HasArtifacts = Artifacts.Count > 0;
    }
}

/// <summary>Display row for one registry artifact with status chip and provenance line.</summary>
/// <param name="Tier">Artifact tier: <c>simulated</c> or <c>measured</c>.</param>
/// <param name="File">Component-relative artifact file name.</param>
/// <param name="Status">Trust status of the artifact.</param>
/// <param name="StatusColor">Chip background color for <paramref name="Status"/>.</param>
/// <param name="Provenance">Human-readable provenance summary (method, tool, author, date).</param>
public record RegistryArtifactDisplay(
    string Tier, string File, string Status, string StatusColor, string Provenance)
{
    /// <summary>Builds a display row from a manifest artifact reference.</summary>
    public static RegistryArtifactDisplay From(string tier, ArtifactRef artifact)
    {
        var provenance = artifact.Provenance;
        var parts = new List<string> { provenance.Method };
        if (!string.IsNullOrEmpty(provenance.Tool))
            parts.Add(provenance.Tool);
        if (!string.IsNullOrEmpty(provenance.CreatedBy))
            parts.Add($"by {provenance.CreatedBy}");
        if (!string.IsNullOrEmpty(provenance.Date))
            parts.Add(provenance.Date);
        if (!string.IsNullOrEmpty(provenance.Fab))
            parts.Add($"fab {provenance.Fab}");

        return new RegistryArtifactDisplay(
            tier, artifact.File, artifact.Status,
            RegistryStatusPresentation.ToColor(artifact.Status),
            string.Join(" \u00b7 ", parts));
    }
}
