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

public partial class CreateCustomPdkViewModel : ObservableObject
{
    private readonly UserPdkStore _store;

    [ObservableProperty]
    private string _pdkName = string.Empty;

    [ObservableProperty]
    private PdkProcessSource _processSource = PdkProcessSource.UseExisting;

    [ObservableProperty]
    private ProcessDefinition? _selectedExistingProcess;

    [ObservableProperty]
    private ProcessDefinition? _selectedTemplate;

    [ObservableProperty]
    private double? _coreThicknessNm;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public IReadOnlyList<ProcessDefinition> AvailableProcesses { get; }

    public ProcessManagementViewModel ProcessDefinitionEditor { get; }

    public string? CreatedFilePath { get; private set; }

    public event EventHandler<string>? PdkCreated;

    public CreateCustomPdkViewModel(
        UserPdkStore store, IReadOnlyList<ProcessDefinition> availableProcesses, ProcessManagementViewModel processDefinitionEditor)
    {
        _store = store;
        AvailableProcesses = availableProcesses;
        ProcessDefinitionEditor = processDefinitionEditor;
        ProcessDefinitionEditor.NewProcessCommand.Execute(null);
        ProcessDefinitionEditor.Xsections.CollectionChanged += OnDefinedXsectionsChanged;
    }

    private void OnDefinedXsectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        CreatePdkCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreatePdk()
    {
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
            StatusText = ex.Message;
        }
    }

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

    partial void OnSelectedTemplateChanged(ProcessDefinition? value)
    {
        if (value == null)
            return;

        ProcessDefinitionEditor.Load(ProcessDefinitionCloner.Clone(value));
        CoreThicknessNm = value.CoreThicknessNm;
    }
}
