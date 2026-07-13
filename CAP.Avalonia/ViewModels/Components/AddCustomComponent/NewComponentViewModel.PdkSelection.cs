using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// PDK-dropdown selection path for <see cref="NewComponentViewModel"/>: the
/// <see cref="PdkChoices"/> list (existing named custom PDKs plus the trailing "New PDK…"
/// sentinel), and the sentinel's modal-creation hook. Split out purely to keep each file under
/// the project's line-count limit; still one partial class, one responsibility.
/// </summary>
public partial class NewComponentViewModel
{
    private List<PdkChoice> _pdkChoices = new();

    /// <summary>The last selected non-sentinel choice, restored when a "New PDK…" creation is cancelled.</summary>
    private PdkChoice? _previousPdkChoice;

    /// <summary>
    /// True only while <see cref="RefreshPdkChoices"/> itself runs. <see
    /// cref="PdkChoice.NewPdkSentinel"/> is a single shared static instance, so a bound
    /// dropdown reacting to <see cref="PdkChoices"/>'s change notification (clearing its
    /// selection, then reselecting whatever it previously held, since that same object is
    /// still present in the refreshed list) can reselect the sentinel a second time
    /// synchronously, nested inside that very notification — re-entering <see
    /// cref="OnSelectedPdkChoiceChanged"/> with the guard in <see
    /// cref="HandleNewPdkSentinelAsync"/> already lifted, reopening the "New PDK…" modal.
    /// Suppressing sentinel handling for just this window closes that gap without swallowing
    /// the real target selection's notifications (that assignment always runs afterwards,
    /// with the flag back off).
    /// </summary>
    private bool _suppressSentinelHandling;

    /// <summary>Named custom PDKs already on disk, refreshed after a new one is created.</summary>
    public IReadOnlyList<UserPdkInfo> AvailableCustomPdks { get; private set; } = Array.Empty<UserPdkInfo>();

    /// <summary>
    /// The PDK dropdown's bindable source: one entry per <see cref="AvailableCustomPdks"/>,
    /// followed by the <see cref="PdkChoice.NewPdkSentinel"/> entry.
    /// </summary>
    public IReadOnlyList<PdkChoice> PdkChoices => _pdkChoices;

    /// <summary>The existing named custom PDK selected as the save target, or null while none is chosen.</summary>
    public UserPdkInfo? SelectedCustomPdk => SelectedPdkChoice?.Pdk;

    /// <summary>The process the component will be saved under: inherited, read-only, from <see cref="SelectedCustomPdk"/>.</summary>
    public ProcessDefinition? SelectedProcess => SelectedCustomPdk?.Process;

    /// <summary>
    /// Modal creation hook invoked when the "New PDK…" sentinel is selected: opens the "create
    /// PDK" dialog and returns the newly created PDK, or null if the user cancelled. A no-op
    /// (selection reverts to the previous choice) when null.
    /// </summary>
    public Func<Task<UserPdkInfo?>>? CreateNewPdk { get; set; }

    /// <summary>
    /// Selecting an existing PDK inherits its process and invalidates the preview (the S-matrix
    /// backend may differ). Selecting the "New PDK…" sentinel invokes <see cref="CreateNewPdk"/>;
    /// selecting a real choice simply remembers it for a future revert.
    /// </summary>
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

    /// <summary>
    /// Runs <see cref="CreateNewPdk"/> after selecting the sentinel: reentrancy-guarded by
    /// <see cref="NewComponentViewModel.IsBusy"/>, refreshes <see cref="PdkChoices"/> and selects
    /// the new PDK on success, or reverts to the previous choice when cancelled (null result), no
    /// hook is wired, or a creation is already in flight.
    /// </summary>
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
        // The created PDK should always be found post-refresh; falling back instead of
        // leaving the sentinel selected is a defensive last resort, not the expected path.
        SelectedPdkChoice = createdChoice
            ?? _previousPdkChoice
            ?? _pdkChoices.FirstOrDefault(c => !c.IsNewPdk);
    }

    /// <summary>Re-reads <see cref="AvailableCustomPdks"/> from the store and rebuilds <see cref="PdkChoices"/>.</summary>
    private void RefreshPdkChoices()
    {
        AvailableCustomPdks = _store.ListCustomPdks();
        _pdkChoices = AvailableCustomPdks.Select(PdkChoice.For).Append(PdkChoice.NewPdkSentinel).ToList();
        OnPropertyChanged(nameof(PdkChoices));
    }
}
