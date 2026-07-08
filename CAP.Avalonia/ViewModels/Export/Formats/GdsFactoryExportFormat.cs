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
    public string Name => "gdsfactory";

    /// <inheritdoc/>
    public string Icon => "🏭";

    /// <inheritdoc/>
    public string Description => "Export a gdsfactory Python script (+ GDS) — standalone or with ubcpdk (SiEPIC) cells";

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
