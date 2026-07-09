using CAP_DataAccess.Persistence.PIR;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;

/// <summary>
/// Backend-selection members of the instance override editor (issue #637): the
/// Nazca | gdsfactory toggle, its help text, and the starter re-seeding. Kept in a
/// separate partial file so the main editor stays within the file-size limit.
/// </summary>
public partial class InstanceNazcaCodeEditorViewModel
{
    /// <summary>Self-contained starter shown when the gdsfactory backend is selected.</summary>
    private const string GdsFactoryOverrideStub =
        "# Override this component's geometry with your own gdsfactory code.\n" +
        "# Assign your component to a variable named `component`.\n" +
        "# Example:\n" +
        "# import gdsfactory as gf\n" +
        "# component = gf.components.mmi1x2()\n";

    /// <summary>
    /// The layout backend this override targets. Switching re-seeds the editor with the
    /// matching starter code and routes Run Preview / Apply to that backend.
    /// </summary>
    [ObservableProperty]
    private OverrideBackend _selectedBackend = OverrideBackend.Nazca;

    /// <summary>True when the gdsfactory backend is selected (drives help text/visibility).</summary>
    public bool IsGdsFactoryBackend => SelectedBackend == OverrideBackend.GdsFactory;

    /// <summary>Two-way bindable toggle for the backend radio buttons.</summary>
    public bool UseGdsFactoryBackend
    {
        get => SelectedBackend == OverrideBackend.GdsFactory;
        set => SelectedBackend = value ? OverrideBackend.GdsFactory : OverrideBackend.Nazca;
    }

    /// <summary>Backend-specific one-line help shown above the editor.</summary>
    public string BackendHelp => IsGdsFactoryBackend
        ? "Write gdsfactory code and assign your component to a variable named `component`."
        : "Write self-contained Nazca code that defines a component() cell.";

    /// <summary>Docs-button caption, per backend.</summary>
    public string DocsButtonLabel => IsGdsFactoryBackend ? "gdsfactory docs ↗" : "Nazca docs ↗";

    /// <summary>Docs URL opened by the docs button, per backend.</summary>
    public string DocsUrl => IsGdsFactoryBackend
        ? "https://gdsfactory.github.io/gdsfactory/"
        : "https://nazca-design.org/manual/";

    /// <summary>Quick-help title in the "?" flyout, per backend.</summary>
    public string QuickHelpTitle => IsGdsFactoryBackend
        ? "gdsfactory — assign a Component to `component`"
        : "Nazca elements — showcase circuit (Insert, or select & Ctrl+C)";

    /// <summary>Example snippet shown in the "?" flyout and inserted by "Insert into editor".</summary>
    public string StarterExample => IsGdsFactoryBackend ? GdsFactoryExample : Services.NazcaCodeExamples.Complex;

    /// <summary>Cheat-sheet line of common elements, per backend.</summary>
    public string QuickHelpElements => IsGdsFactoryBackend
        ? "gf.components: straight(length, width) · bend_euler(radius, angle) · mmi1x2() · mmi2x2() · "
          + "coupler(gap, length) · ring_single(radius) · taper(length, width1, width2) · grating_coupler_elliptical()"
        : "strt(length, width) · bend(radius, angle, width) · taper(length, width1, width2) · euler(width, radius, angle) · "
          + "sinebend(width, distance, offset) · cobra(xya=(x,y,a), width1, width2) · ring(radius, width) · Pin(name).put(x, y, angle)";

    /// <summary>Runnable gdsfactory starter (verified against gdsfactory 9.x).</summary>
    private const string GdsFactoryExample =
        "import gdsfactory as gf\n\n" +
        "# Assign your component to `component`. Ports become the instance pins.\n" +
        "component = gf.components.mmi1x2()\n";

    partial void OnSelectedBackendChanged(OverrideBackend value)
    {
        OnPropertyChanged(nameof(IsGdsFactoryBackend));
        OnPropertyChanged(nameof(UseGdsFactoryBackend));
        OnPropertyChanged(nameof(BackendHelp));
        OnPropertyChanged(nameof(DocsButtonLabel));
        OnPropertyChanged(nameof(DocsUrl));
        OnPropertyChanged(nameof(QuickHelpTitle));
        OnPropertyChanged(nameof(StarterExample));
        OnPropertyChanged(nameof(QuickHelpElements));
        // Re-seed only when the user hasn't authored code yet (still on a starter), so a
        // toggle never discards real work.
        if (IsOnAStarter())
        {
            Code = value == OverrideBackend.GdsFactory ? GdsFactoryOverrideStub : OverrideStub;
            _originalSourceCode = value == OverrideBackend.GdsFactory ? null : _originalSourceCode;
        }
        IsValid = false;
        ApplyOverrideCommand.NotifyCanExecuteChanged();
    }

    private bool IsOnAStarter()
    {
        var c = (Code ?? string.Empty).Trim();
        return c.Length == 0 || c == OverrideStub.Trim() || c == GdsFactoryOverrideStub.Trim()
            || (_originalSourceCode != null && c == _originalSourceCode.Trim());
    }

    /// <summary>
    /// Renders the current code through the backend-appropriate preview service. Returns
    /// null (with <see cref="PreviewError"/> set) when the required service is missing:
    /// gdsfactory always runs raw-code; Nazca runs raw-code for edited code or module mode
    /// for the unedited original.
    /// </summary>
    private async System.Threading.Tasks.Task<CAP_Core.Export.NazcaPreviewResult?> RenderForBackendAsync()
    {
        if (IsGdsFactoryBackend)
        {
            if (_gdsFactoryPreviewService == null)
            {
                PreviewError = "gdsfactory preview service unavailable.";
                return null;
            }
            return await _gdsFactoryPreviewService.RenderRawCodeAsync(Code);
        }

        return IsCustomCode
            ? await _previewService!.RenderRawCodeAsync(Code)
            : await _previewService!.RenderAsync(_moduleName, _nazcaFunction, _nazcaParameters);
    }
}
