using System;
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Builds the <see cref="NewComponentViewModel"/> offered by the "New Component" window
/// (issue #656) and wires its <see cref="NewComponentViewModel.Saved"/> event: on save, reads
/// the saved process's user-PDK file back (so the registered PDK name always matches what
/// <see cref="UserPdkStore"/> wrote to disk) and hands the draft to <paramref name="register"/>
/// in <see cref="BuildViewModel"/>. Extracted out of <c>LeftPanelViewModel.OpenNewComponent</c>
/// to keep that command thin.
/// </summary>
public static class NewComponentWindowLauncher
{
    /// <summary>
    /// Creates the view model, offering every currently loaded PDK's fabrication process as a
    /// save target (a process-less/tool PDK has nowhere physically meaningful to save into).
    /// </summary>
    public static NewComponentViewModel BuildViewModel(
        AddCustomComponentDependencies deps, PdkLoader pdkLoader, IReadOnlyList<PdkDraft> loadedPdks,
        Action<PdkComponentDraft, string, string> register)
    {
        var processes = loadedPdks.Where(d => d.Process != null).Select(d => d.Process!).ToList();
        var vm = new NewComponentViewModel(deps.Extractor, deps.Fdtd, deps.UserPdkStore, processes);
        vm.Saved += (_, _) => OnSaved(vm, deps.UserPdkStore, pdkLoader, register);
        return vm;
    }

    private static void OnSaved(
        NewComponentViewModel vm, UserPdkStore userPdkStore, PdkLoader pdkLoader,
        Action<PdkComponentDraft, string, string> register)
    {
        if (vm.SavedDraft is null || vm.SelectedProcess is null) return;

        var filePath = userPdkStore.ResolvePath(vm.SelectedProcess);
        var pdk = pdkLoader.LoadFromFile(filePath);
        register(vm.SavedDraft, pdk.Name, filePath);
    }
}
