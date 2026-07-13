using System;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// PDK-creation mode for <see cref="ProcessManagementViewModel"/>: lets the "New Component"
/// wizard reuse this dialog's process editor (layers/cross-sections/materials) to build a
/// brand-new, named user PDK from scratch, distinct from its normal role of viewing/editing the
/// design's already-active process. Deliberately never touches
/// <c>FileOperationsViewModel</c>/<c>ActiveProcess</c>/<c>SetActiveProcess</c> — wiring a newly
/// created PDK into the active design is out of scope here (tracked separately as bug #726).
/// </summary>
public partial class ProcessManagementViewModel
{
    /// <summary>True while the dialog is being used to create a new named PDK rather than to
    /// view/edit the design's active process.</summary>
    [ObservableProperty]
    private bool _isPdkCreationMode;

    /// <summary>The name for the new PDK being created, bound to the "PDK name" field.</summary>
    [ObservableProperty]
    private string _pdkName = string.Empty;

    /// <summary>
    /// Creates a new named user PDK from the given name and process definition, returning the
    /// path it was written to. Wired by the caller to
    /// <c>UserPdkStore.CreateNamedPdkWithProcess</c>; left null (e.g. in tests) disables
    /// <see cref="CreatePdkCommand"/>.
    /// </summary>
    public Func<string, ProcessDefinition, string>? CreateUserPdk { get; set; }

    /// <summary>
    /// Optional collision check for the chosen PDK name. Wired by the caller to
    /// <c>UserPdkStore.NamedPdkExists</c>; left null skips the check.
    /// </summary>
    public Func<string, bool>? PdkNameExists { get; set; }

    /// <summary>Raised with the written PDK's file path after <see cref="CreatePdkCommand"/> succeeds.</summary>
    public event EventHandler<string>? PdkCreated;

    /// <summary>
    /// Switches the dialog into PDK-creation mode and starts it with a fresh, blank process
    /// (same defaults as the regular "New process" action) so the user can pick a preset or
    /// build layers/cross-sections/materials by hand. Does not read or write
    /// <c>ActiveProcess</c> — this mode is only ever about the process being newly authored.
    /// </summary>
    public void EnterPdkCreationMode()
    {
        IsPdkCreationMode = true;
        NewProcess();
    }

    /// <summary>
    /// Writes the currently edited process to a new named user PDK via <see cref="CreateUserPdk"/>
    /// and raises <see cref="PdkCreated"/> with the resulting path. If <see cref="PdkNameExists"/>
    /// reports a collision, no PDK is written; the caller can offer an overwrite confirmation in a
    /// later iteration — for now this just surfaces the collision via <see cref="StatusText"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreatePdk))]
    private void CreatePdk()
    {
        if (PdkNameExists?.Invoke(PdkName) == true)
        {
            StatusText = $"A PDK named '{PdkName}' already exists. Choose a different name.";
            return;
        }

        var path = CreateUserPdk!(PdkName, ToProcess());
        PdkCreated?.Invoke(this, path);
    }

    private bool CanCreatePdk() => IsPdkCreationMode && !string.IsNullOrWhiteSpace(PdkName) && CreateUserPdk != null;

    partial void OnPdkNameChanged(string value) => CreatePdkCommand.NotifyCanExecuteChanged();

    partial void OnIsPdkCreationModeChanged(bool value) => CreatePdkCommand.NotifyCanExecuteChanged();
}
