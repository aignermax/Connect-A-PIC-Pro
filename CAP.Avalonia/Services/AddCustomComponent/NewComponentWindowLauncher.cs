using System;
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

public static class NewComponentWindowLauncher
{
    public static NewComponentViewModel BuildViewModel(
        AddCustomComponentDependencies deps, PdkLoader pdkLoader, IReadOnlyList<PdkDraft> loadedPdks,
        Action<PdkComponentDraft, string, string, bool> register,
        Action<string, string>? removeMigratedTemplate = null)
    {
        var processes = loadedPdks.Where(d => d.Process != null).Select(d => d.Process!).ToList();
        var vm = new NewComponentViewModel(deps.Extractor, deps.Fdtd, deps.UserPdkStore, processes,
            deps.ErrorConsole);
        vm.Saved += (_, _) => OnSaved(vm, pdkLoader, register, removeMigratedTemplate);
        return vm;
    }

    private static void OnSaved(
        NewComponentViewModel vm, PdkLoader pdkLoader,
        Action<PdkComponentDraft, string, string, bool> register,
        Action<string, string>? removeMigratedTemplate)
    {
        if (vm.SavedDraft is null || vm.SavedFilePath is null) return;

        var filePath = vm.SavedFilePath;
        var pdk = pdkLoader.LoadFromFileForEditing(filePath);
        register(vm.SavedDraft, pdk.Name, filePath, vm.SavedViaPendingBundledFork);

        if (vm.MigratedFromPdkName is { } fromPdk)
            removeMigratedTemplate?.Invoke(fromPdk, vm.MigratedFromComponentName ?? vm.SavedDraft.Name);
    }
}
