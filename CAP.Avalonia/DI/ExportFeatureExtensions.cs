using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers all export format services and ViewModels (GDS, PhotonTorch, VerilogA).
/// All export ViewModels are Singletons so that dialog DataContexts and
/// FileOperations commands share the same instance and state.
/// </summary>
internal static class ExportFeatureExtensions
{
    /// <summary>
    /// Adds GDS, PhotonTorch, and VerilogA export services and ViewModels.
    /// </summary>
    public static IServiceCollection AddExportFeature(this IServiceCollection services)
    {
        services.AddSingleton<GdsExportService>();
        services.AddSingleton<GdsExportViewModel>(sp =>
        {
            var vm = new GdsExportViewModel(
                sp.GetRequiredService<GdsExportService>(),
                sp.GetRequiredService<CAP_Core.ErrorConsoleService>());
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            vm.Initialize(prefs.GetCustomPythonPath());
            vm.OnPythonPathChanged = path => prefs.SetCustomPythonPath(path);

            // Managed-environment integration (lazy resolution — the registry and the
            // env-manager ViewModel are resolved when the delegate fires, not at build time).
            vm.ManagedEnvironmentsProvider = () =>
                sp.GetRequiredService<PythonEnvironmentRegistry>().GetAll()
                    .Where(e => e.IsHealthy)
                    .Select(e => new ManagedEnvCandidate(
                        e.Name,
                        e.PythonExecutable,
                        $"Managed · {e.Name} · Python {e.PythonVersion ?? "?"} · Nazca {e.NazcaVersion ?? "?"}"))
                    .ToList();
            vm.ActivateManagedEnvironment = name =>
                sp.GetRequiredService<PythonEnvironmentRegistry>().SetActive(name);
            vm.RequestNazcaInstall = () =>
                _ = sp.GetRequiredService<PythonEnvironmentManagerViewModel>()
                    .StartDefaultNazcaInstallAsync();
            return vm;
        });

        services.AddSingleton<PhotonTorchExporter>();
        services.AddSingleton<PhotonTorchExportViewModel>();

        services.AddSingleton<GdsFactoryExportViewModel>(sp =>
        {
            var vm = new GdsFactoryExportViewModel(
                sp.GetRequiredService<CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel>(),
                sp.GetRequiredService<GdsExportService>(),
                sp.GetService<IUrlLauncher>(),
                sp.GetService<CAP_Core.ErrorConsoleService>());
            // Auto-install gdsfactory on export (env-manager slice resolved lazily so the
            // export slice never imports it directly).
            vm.EnsureGdsFactoryAsync = (progress, ct) =>
                sp.GetRequiredService<PythonEnvironmentManagerViewModel>()
                    .EnsureGdsFactoryInstalledAsync(progress, ct);
            // Mixed-backend export: the classifier needs the loaded library
            // (raw-code backend lookup) and the configured nazca exporter (lazy resolution).
            vm.TemplateLibraryProvider = () =>
                sp.GetRequiredService<CAP.Avalonia.ViewModels.Panels.LeftPanelViewModel>().AllTemplates;
            vm.NazcaExporterProvider = () => sp.GetRequiredService<SimpleNazcaExporter>();
            return vm;
        });

        services.AddSingleton<VerilogAExporter>();
        services.AddSingleton<VerilogAFileWriter>();
        services.AddSingleton<VerilogAExportViewModel>();

        services.AddSingleton<SaxExporter>();

        // Netlist view/export (gdsfactory YAML, issue #687)
        services.AddSingleton<CAP.Avalonia.ViewModels.Export.Netlist.NetlistViewModel>();

        return services;
    }
}
