using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private List<PdkChoice> _pdkChoices = new();

    private PdkChoice? _previousPdkChoice;

    private bool _suppressSentinelHandling;

    public IReadOnlyList<UserPdkInfo> AvailableCustomPdks { get; private set; } = Array.Empty<UserPdkInfo>();

    public IReadOnlyList<PdkChoice> PdkChoices => _pdkChoices;

    public UserPdkInfo? SelectedCustomPdk => SelectedPdkChoice?.Pdk;

    public ProcessDefinition? SelectedProcess => SelectedCustomPdk?.Process;

    public Func<Task<UserPdkInfo?>>? CreateNewPdk { get; set; }

    partial void OnSelectedPdkChoiceChanged(PdkChoice? value)
    {
        if (_suppressSentinelHandling) return;

        if (value is { IsNewPdk: false })
        {
            _previousPdkChoice = value;
        }

        OnPropertyChanged(nameof(SelectedCustomPdk));
        OnPropertyChanged(nameof(SelectedProcess));
        InvalidatePreview();
        SaveCommand.NotifyCanExecuteChanged();

        if (value is { IsNewPdk: true })
        {
            _ = HandleNewPdkSentinelAsync();
        }
    }

    private async Task HandleNewPdkSentinelAsync()
    {
        if (IsBusy || CreateNewPdk is null)
        {
            SelectedPdkChoice = _previousPdkChoice;
            return;
        }

        IsBusy = true;
        UserPdkInfo? created;
        try
        {
            created = await CreateNewPdk();
        }
        finally
        {
            IsBusy = false;
        }

        if (created is null)
        {
            SelectedPdkChoice = _previousPdkChoice;
            return;
        }

        try
        {
            _suppressSentinelHandling = true;
            RefreshPdkChoices();
        }
        finally
        {
            _suppressSentinelHandling = false;
        }

        var createdChoice = _pdkChoices.FirstOrDefault(
            c => !c.IsNewPdk && c.Pdk!.FilePath == created.FilePath);
        SelectedPdkChoice = createdChoice
            ?? _previousPdkChoice
            ?? _pdkChoices.FirstOrDefault(c => !c.IsNewPdk);
    }

    private void RefreshPdkChoices()
    {
        AvailableCustomPdks = _store.ListCustomPdks();
        _pdkChoices = AvailableCustomPdks.Select(PdkChoice.For).Append(PdkChoice.NewPdkSentinel).ToList();
        OnPropertyChanged(nameof(PdkChoices));
    }
}
