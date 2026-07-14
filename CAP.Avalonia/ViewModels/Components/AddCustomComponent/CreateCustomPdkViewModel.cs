using System;
using System.Collections.Generic;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// Purpose-built "Create Custom PDK" dialog view model (issue #729 follow-up design,
/// 2026-07-14): lets the user name a new user PDK and give it a fabrication process, either
/// adopted from an already-loaded process or freshly authored, then writes it via
/// <see cref="UserPdkStore.CreateNamedPdkWithProcess"/>. Deliberately separate from
/// <see cref="ProcessManagementViewModel"/>'s normal view/edit role for an already-active
/// process — this dialog only ever creates a brand-new named PDK, never edits an existing one.
/// </summary>
public partial class CreateCustomPdkViewModel : ObservableObject
{
    private readonly UserPdkStore _store;

    /// <summary>Name for the new user PDK; must be non-empty and not collide with an existing named PDK.</summary>
    [ObservableProperty]
    private string _pdkName = string.Empty;

    /// <summary>Whether the process is adopted from <see cref="AvailableProcesses"/> or freshly defined.</summary>
    [ObservableProperty]
    private PdkProcessSource _processSource = PdkProcessSource.UseExisting;

    /// <summary>The process picked in the "Use existing" dropdown, or null if none selected yet.</summary>
    [ObservableProperty]
    private ProcessDefinition? _selectedExistingProcess;

    /// <summary>Status / result message, notably the name-collision warning.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Already-loaded processes offered by the "Use existing" dropdown.</summary>
    public IReadOnlyList<ProcessDefinition> AvailableProcesses { get; }

    /// <summary>
    /// Embedded process-definition editor reused for the "Define new" path (layer stack,
    /// cross-sections, materials, <see cref="ProcessManagementViewModel.ToProcess"/>) — no
    /// duplication of that grid-editing logic. Its edit-existing-process affordances (import,
    /// save-to-file, preset loader) are simply not surfaced by this dialog's view.
    /// </summary>
    public ProcessManagementViewModel ProcessDefinitionEditor { get; }

    /// <summary>The file path the new PDK was written to, set after a successful <see cref="CreatePdkCommand"/>.</summary>
    public string? CreatedFilePath { get; private set; }

    /// <summary>Raised with the written PDK's file path after <see cref="CreatePdkCommand"/> succeeds.</summary>
    public event EventHandler<string>? PdkCreated;

    /// <summary>Initialises the dialog with the store to persist into and the process choices it offers.</summary>
    public CreateCustomPdkViewModel(
        UserPdkStore store, IReadOnlyList<ProcessDefinition> availableProcesses, ProcessManagementViewModel processDefinitionEditor)
    {
        _store = store;
        AvailableProcesses = availableProcesses;
        ProcessDefinitionEditor = processDefinitionEditor;
        ProcessDefinitionEditor.NewProcessCommand.Execute(null);
    }

    /// <summary>
    /// Builds the process from the chosen source, refuses on a name collision, and otherwise
    /// creates the named user PDK, raising <see cref="PdkCreated"/> with the written path.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreatePdk()
    {
        if (_store.NamedPdkExists(PdkName))
        {
            StatusText = $"A PDK named '{PdkName}' already exists.";
            return;
        }

        var process = ProcessSource == PdkProcessSource.UseExisting
            ? SelectedExistingProcess!
            : ProcessDefinitionEditor.ToProcess();

        CreatedFilePath = _store.CreateNamedPdkWithProcess(PdkName, process, "gdsfactory", null);
        PdkCreated?.Invoke(this, CreatedFilePath);
    }

    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(PdkName) && (ProcessSource == PdkProcessSource.DefineNew || SelectedExistingProcess != null);

    partial void OnPdkNameChanged(string value) => CreatePdkCommand.NotifyCanExecuteChanged();

    partial void OnProcessSourceChanged(PdkProcessSource value) => CreatePdkCommand.NotifyCanExecuteChanged();

    partial void OnSelectedExistingProcessChanged(ProcessDefinition? value) => CreatePdkCommand.NotifyCanExecuteChanged();
}
