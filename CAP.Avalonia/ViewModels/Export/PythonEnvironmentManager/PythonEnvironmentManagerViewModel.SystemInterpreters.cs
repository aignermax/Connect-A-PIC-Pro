using System.Collections.ObjectModel;
using CAP_Core.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

/// <summary>
/// System-interpreter half of the manager (issue #645): the Python Environments tab is the
/// single interpreter hub, so alongside the managed environments it also lists the system
/// Pythons discovered by <see cref="PythonDiscoveryService"/> — each with its Nazca and
/// gdsfactory version — and lets the user activate one. Activation routes through
/// <see cref="PythonEnvironmentRegistry.SetActiveExternalPath"/>, the same channel a managed
/// activation uses, so export and preview pick it up immediately.
/// </summary>
public partial class PythonEnvironmentManagerViewModel
{
    private List<PythonDiscoveryService.PythonInstallation> _discoveredInterpreters = new();
    private int _discoveryGeneration;

    /// <summary>Discovered system Pythons, shown below the managed environments.</summary>
    public ObservableCollection<InterpreterOption> SystemInterpreters { get; } = new();

    [ObservableProperty]
    private bool _isDiscoveringInterpreters;

    /// <summary>True when discovery has finished and found no system interpreters.</summary>
    public bool ShowNoSystemInterpretersHint =>
        !IsDiscoveringInterpreters && SystemInterpreters.Count == 0;

    partial void OnIsDiscoveringInterpretersChanged(bool value) =>
        OnPropertyChanged(nameof(ShowNoSystemInterpretersHint));

    /// <summary>
    /// Discovers system Python interpreters and rebuilds <see cref="SystemInterpreters"/>.
    /// A newer run supersedes an in-flight one so rapid tab switches cannot duplicate the
    /// list. Safe to call on navigation.
    /// </summary>
    [RelayCommand]
    public async Task RefreshSystemInterpretersAsync()
    {
        var generation = ++_discoveryGeneration;
        IsDiscoveringInterpreters = true;
        try
        {
            var found = await _discovery.DiscoverPythonWithNazcaAsync();
            if (generation != _discoveryGeneration)
                return;   // a newer discovery owns the list now

            _discoveredInterpreters = found;
            RemarkSystemInterpreters();
        }
        finally
        {
            if (generation == _discoveryGeneration)
                IsDiscoveringInterpreters = false;
        }
    }

    /// <summary>
    /// Activates a discovered system interpreter: clears any managed active selection and
    /// pushes this path into export/preview via the registry callback, then refreshes both
    /// the managed list and the system markers so exactly one entry shows as active.
    /// </summary>
    /// <param name="option">The system interpreter the user clicked.</param>
    [RelayCommand]
    private void ActivateSystemInterpreter(InterpreterOption option)
    {
        if (option == null) return;

        _registry.SetActiveExternalPath(option.Path);
        RefreshList();
        RemarkSystemInterpreters();
        ProgressText = $"Active interpreter set to {option.Path}.";
    }

    /// <summary>
    /// Rebuilds <see cref="SystemInterpreters"/> from the cached discovery result, refreshing
    /// each entry's active marker against the currently active interpreter path. Cheap — no
    /// subprocess probing — so it runs after every activation.
    /// </summary>
    internal void RemarkSystemInterpreters()
    {
        var activePath = _getActiveInterpreterPath();
        SystemInterpreters.Clear();
        foreach (var install in _discoveredInterpreters)
            SystemInterpreters.Add(new InterpreterOption(
                install.DisplayText,
                install.Path,
                IsActivePath(install.Path, activePath),
                ManagedName: null));
        OnPropertyChanged(nameof(ShowNoSystemInterpretersHint));
    }

    private static bool IsActivePath(string path, string? activePath) =>
        !string.IsNullOrEmpty(activePath)
        && string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase);
}
