using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using CAP.Avalonia.Services.AddCustomComponent;
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

    /// <summary>
    /// The process picked in the "Define new" path's "Start from template" dropdown, or null if
    /// none chosen. Selecting one prefills <see cref="ProcessDefinitionEditor"/> (name, layers,
    /// cross-sections, materials) and <see cref="CoreThicknessNm"/> as a starting point only —
    /// the user is free to edit every field afterwards, and <c>CreatePdk</c> persists whatever
    /// ends up in the editor, not this template.
    /// </summary>
    [ObservableProperty]
    private ProcessDefinition? _selectedTemplate;

    /// <summary>
    /// Core waveguide-layer thickness in nm for a "Define new" process. Required for the created
    /// PDK's process fingerprint to be complete (<see cref="CAP_Core.Components.Process.ProcessFingerprint.IsSpecified"/>
    /// needs core material + thickness + cladding) — without it the new PDK would never match the
    /// active process by value and would stay invisible/locked (issue #570). Only used for the
    /// "Define new" path; an adopted existing process already carries its own thickness.
    /// </summary>
    [ObservableProperty]
    private double? _coreThicknessNm;

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
        // A "Define new" process only becomes creatable once it has at least one cross-section
        // (see CanCreate); re-evaluate availability whenever that collection changes.
        ProcessDefinitionEditor.Xsections.CollectionChanged += OnDefinedXsectionsChanged;
    }

    private void OnDefinedXsectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        CreatePdkCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Builds the process from the chosen source, refuses on a name collision, and otherwise
    /// creates the named user PDK, raising <see cref="PdkCreated"/> with the written path.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreatePdk()
    {
        // Collision is checked against the stored DISPLAY names, not the slugged file name, so
        // "My Lib" vs "My-Lib" are not conflated and a name that slugs to the "custom" fallback
        // does not falsely block an unrelated name.
        if (_store.ListCustomPdks().Any(p => string.Equals(p.Name, PdkName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"A PDK named '{PdkName}' already exists.";
            return;
        }

        var process = BuildProcess();

        try
        {
            var path = _store.CreateNamedPdkWithProcess(PdkName, process, "gdsfactory", null);
            CreatedFilePath = path;
            PdkCreated?.Invoke(this, path);
        }
        catch (InvalidOperationException ex)
        {
            // The store keys files by name-slug, so two distinct display names can still collide
            // at the file level. Surface that instead of letting the exception crash the dialog.
            StatusText = ex.Message;
        }
    }

    /// <summary>
    /// Builds the process for the new PDK. The "Define new" path stamps the user-entered
    /// core thickness onto the editor's process so the resulting fingerprint is complete.
    /// </summary>
    private ProcessDefinition BuildProcess()
    {
        if (ProcessSource == PdkProcessSource.UseExisting)
        {
            return SelectedExistingProcess!;
        }

        var process = ProcessDefinitionEditor.ToProcess();
        process.CoreThicknessNm = CoreThicknessNm;
        return process;
    }

    private bool CanCreate()
    {
        if (string.IsNullOrWhiteSpace(PdkName))
        {
            return false;
        }

        return ProcessSource == PdkProcessSource.DefineNew
            ? ProcessDefinitionEditor.Xsections.Count > 0
            : SelectedExistingProcess != null;
    }

    partial void OnPdkNameChanged(string value) => CreatePdkCommand.NotifyCanExecuteChanged();

    partial void OnProcessSourceChanged(PdkProcessSource value) => CreatePdkCommand.NotifyCanExecuteChanged();

    partial void OnSelectedExistingProcessChanged(ProcessDefinition? value) => CreatePdkCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Prefills the "Define new" editor from the chosen template. A no-op on clearing the
    /// selection (value == null) so the editor is left exactly as the user last edited it.
    /// Loads a deep copy, never <paramref name="value"/> itself: <see cref="AvailableProcesses"/>
    /// entries can be the live, in-memory <see cref="ProcessDefinition"/> of an already-loaded PDK
    /// (see <c>MainWindow.axaml.cs</c>'s <c>d.Process!</c> wiring), and <see cref="ProcessManagementViewModel.Load"/>
    /// only copies collection references — editing the prefilled grid rows would otherwise mutate
    /// that other PDK's process object in place.
    /// </summary>
    partial void OnSelectedTemplateChanged(ProcessDefinition? value)
    {
        if (value == null)
            return;

        // Deep-copies via the shared ProcessDefinitionCloner (issue #733 review, Finding 3) —
        // the same helper LoadForSinglePdkEdit uses — so this dialog never aliases the
        // template's rows (which can be another already-loaded PDK's live in-memory process).
        ProcessDefinitionEditor.Load(ProcessDefinitionCloner.Clone(value));
        CoreThicknessNm = value.CoreThicknessNm;
    }
}
