using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CAP_Core.Components.Process;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Process;

/// <summary>One selectable option in the New-Design process dialog (issue #570).</summary>
public sealed record ProcessChoiceItem(string Title, string Subtitle, ActiveProcessSelection Selection, bool IsPlayground);

/// <summary>
/// Lets the user consciously pick the fabrication process for a new design, or Playground
/// (mix anything, not manufacturable). Produces an <see cref="ActiveProcessSelection"/> (#570).
/// </summary>
public partial class ProcessSelectionViewModel : ObservableObject
{
    /// <summary>Available processes (derived groups) plus a trailing Playground option.</summary>
    public ObservableCollection<ProcessChoiceItem> Choices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private ProcessChoiceItem? _selectedChoice;

    /// <summary>The confirmed selection, or null until the user confirms.</summary>
    public ActiveProcessSelection? Result { get; private set; }

    /// <summary>True when a choice is selected.</summary>
    public bool CanConfirm => SelectedChoice != null;

    /// <summary>Builds the choice list from the derived process groups.</summary>
    public ProcessSelectionViewModel(IReadOnlyList<ProcessGroup> groups)
    {
        foreach (var g in groups)
            Choices.Add(new ProcessChoiceItem(
                g.DisplayName,
                $"{g.MemberPdkNames.Count} PDK(s): {string.Join(", ", g.MemberPdkNames)}",
                ActiveProcessSelection.ForGroup(g), IsPlayground: false));

        Choices.Add(new ProcessChoiceItem(
            "Playground", "Mix any components — not manufacturable",
            ActiveProcessSelection.Playground(), IsPlayground: true));
    }

    /// <summary>Confirms the current selection into <see cref="Result"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Result = SelectedChoice?.Selection;
}
