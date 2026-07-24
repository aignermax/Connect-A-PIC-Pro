using CAP_Core.ComponentRegistry.RegistryClient;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// One row of the registry browser list: a read-only view over a
/// <see cref="RegistryIndexEntry"/> with tier badges, a status chip and a
/// process-mismatch flag against the currently active fabrication process.
/// </summary>
public partial class RegistryComponentItemViewModel : ObservableObject
{
    private readonly RegistryIndexEntry _entry;

    /// <summary>Wraps a registry index entry for display.</summary>
    public RegistryComponentItemViewModel(RegistryIndexEntry entry)
    {
        _entry = entry;
    }

    /// <summary>The wrapped index entry (e.g. for preview fetches via the client).</summary>
    public RegistryIndexEntry Entry => _entry;

    /// <summary>Registry-wide component identifier.</summary>
    public string Id => _entry.Id;

    /// <summary>Human-readable component name.</summary>
    public string Name => _entry.Name;

    /// <summary>Short description of the component.</summary>
    public string Description => _entry.Description;

    /// <summary>Identifier of the fabrication process the component targets.</summary>
    public string ProcessId => _entry.Process;

    /// <summary>Number of optical ports.</summary>
    public int PortCount => _entry.PortCount;

    /// <summary>Repo-relative path to the component manifest, used for detail loading.</summary>
    public string ManifestPath => _entry.Path;

    /// <summary>True when a geometry artifact is published.</summary>
    public bool HasGeometry => _entry.Tiers.Geometry;

    /// <summary>True when a simulated S-matrix artifact is published.</summary>
    public bool HasSimulated => _entry.Tiers.Simulated;

    /// <summary>True when a fab-measured artifact is published.</summary>
    public bool HasMeasured => _entry.Tiers.Measured;

    /// <summary>Best trust status across all tiers (demo / unverified / verified / disputed).</summary>
    public string Status => _entry.BestStatus;

    /// <summary>Chip background color for <see cref="Status"/>.</summary>
    public string StatusColor => RegistryStatusPresentation.ToColor(_entry.BestStatus);

    /// <summary>Tier badge line, e.g. <c>geometry ✗ · simulated ✓ · measured ✗</c>.</summary>
    public string TiersText => RegistryStatusPresentation.BuildTierText(
        HasGeometry, HasSimulated, HasMeasured);

    /// <summary>Process id and port count line, e.g. <c>generic-si220 · 3 ports</c>.</summary>
    public string ProcessAndPortsText => $"{ProcessId} \u00b7 {PortCount} ports";

    /// <summary>
    /// True when the component targets a process different from the active one.
    /// Such components are display-only and must not be placed on the canvas.
    /// </summary>
    [ObservableProperty]
    private bool _isProcessMismatch;

    /// <summary>
    /// Parseable preview SVG text once the async preview fetch succeeded;
    /// empty while loading, when the entry declares no preview, or when the
    /// download/parse failed — the tile then keeps its placeholder pictogram.
    /// </summary>
    [ObservableProperty]
    private string _previewSvg = "";

    /// <summary>
    /// Recomputes <see cref="IsProcessMismatch"/> against the active process id.
    /// A null/empty active process means "no process loaded" and never mismatches.
    /// </summary>
    public void UpdateProcessMismatch(string? activeProcessId) =>
        IsProcessMismatch = !string.IsNullOrEmpty(activeProcessId) &&
            !string.Equals(activeProcessId, _entry.Process, StringComparison.OrdinalIgnoreCase);
}
