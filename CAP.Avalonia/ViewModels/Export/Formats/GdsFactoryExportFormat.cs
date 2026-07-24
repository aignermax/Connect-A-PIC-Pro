using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export.Formats;

/// <summary>
/// Export format for gdsfactory layout scripts (#581): opens an options dialog for
/// the component mode (standalone stubs vs. ubcpdk cells) and GDS generation.
/// </summary>
public class GdsFactoryExportFormat : IExportFormat
{
    private readonly AsyncRelayCommand _exportCommand;

    /// <inheritdoc/>
    public string Name => "Whole layout → GDS";

    /// <inheritdoc/>
    public string Icon => "🏭";

    /// <inheritdoc/>
    public string Description => "One merged GDS for the whole design — every component rendered by its own engine (gdsfactory, ubcpdk (SiEPIC), nazca) and merged automatically; editable Python script(s)";

    /// <inheritdoc/>
    public string Background => "#3d5d4d";

    /// <inheritdoc/>
    public IAsyncRelayCommand ExportCommand => _exportCommand;

    /// <summary>
    /// Callback that opens the gdsfactory export options dialog. Wired by the UI layer
    /// (<c>Views.Dialogs.ExportDialogWiring.Wire</c>); invoking the command before that
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public Func<Task>? ShowOptionsDialogAsync { get; set; }

    /// <summary>Initializes the gdsfactory export format adapter.</summary>
    public GdsFactoryExportFormat()
    {
        _exportCommand = new AsyncRelayCommand(RunExportFlowAsync);
    }

    private async Task RunExportFlowAsync()
    {
        if (ShowOptionsDialogAsync == null)
            throw new InvalidOperationException(
                $"{nameof(GdsFactoryExportFormat)}.{nameof(ShowOptionsDialogAsync)} has not been wired. " +
                "The UI layer (Views.Dialogs.ExportDialogWiring.Wire) must set this callback before the export command can run.");
        await ShowOptionsDialogAsync();
    }
}
