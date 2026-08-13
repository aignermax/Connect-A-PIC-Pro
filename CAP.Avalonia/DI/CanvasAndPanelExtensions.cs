using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;
using CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.ViewModels.Properties;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP_Core.Export;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the design canvas, viewport, and all panel ViewModels.
/// </summary>
internal static class CanvasAndPanelExtensions
{
    /// <summary>
    /// Adds DesignCanvasViewModel, ViewportControlViewModel, and the three panel
    /// ViewModels together with all their sub-ViewModels.
    /// </summary>
    public static IServiceCollection AddCanvasAndPanels(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var canvas = new DesignCanvasViewModel();

            // Restore the user's last choice, then persist any further toggle (Settings →
            // Routing → Enable Diagonal) — the checkbox otherwise silently reverted to off
            // on every app restart.
            var preferences = sp.GetRequiredService<UserPreferencesService>();
            canvas.UseDiagonalRouting = preferences.GetUseDiagonalRouting();
            canvas.PreferDirectStyledRoutes = preferences.GetPreferDirectStyledRoutes();
            canvas.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DesignCanvasViewModel.UseDiagonalRouting))
                    preferences.SetUseDiagonalRouting(canvas.UseDiagonalRouting);
                if (e.PropertyName == nameof(DesignCanvasViewModel.PreferDirectStyledRoutes))
                    preferences.SetPreferDirectStyledRoutes(canvas.PreferDirectStyledRoutes);
            };

            return canvas;
        });
        services.AddSingleton<ViewportControlViewModel>();

        // GDS preview render service — shared by the canvas overlay and the
        // component-library thumbnails. Uses the Nazca backend for Nazca components and the
        // gdsfactory backend for gdsfactory-native components (CornerStone SiN etc., #570).
        services.AddSingleton(sp =>
            new GdsPreviewRenderService(
                sp.GetRequiredService<NazcaComponentPreviewService>(),
                sp.GetRequiredService<GdsFactoryComponentPreviewService>()));

        // Left panel sub-ViewModels
        services.AddTransient<HierarchyPanelViewModel>();
        services.AddSingleton<PdkManagerViewModel>();
        services.AddTransient<ComponentLibraryViewModel>();

        // Right panel sub-ViewModels
        services.AddSingleton<ChipSizeViewModel>();
        services.AddTransient<ParameterSweepViewModel>();
        services.AddTransient<ViewModels.Analysis.CircuitOptimization.CircuitOptimizationViewModel>();
        services.AddTransient<OnaSweepViewModel>();
        services.AddTransient<RoutingDiagnosticsViewModel>();
        services.AddTransient<DesignValidationViewModel>();
        services.AddTransient<ComponentDimensionDiagnosticsViewModel>();
        services.AddTransient<ComponentDimensionViewModel>();
        services.AddTransient<ExportValidationViewModel>();
        services.AddTransient<SMatrixPerformanceViewModel>();
        services.AddTransient<CompressLayoutViewModel>();
        services.AddTransient<GroupSMatrixViewModel>();
        services.AddTransient<ArchitectureReportViewModel>();
        services.AddTransient<PdkConsistencyViewModel>();
        services.AddTransient<TimeDomainViewModel>();
        services.AddTransient<EyeDiagramViewModel>();
        services.AddTransient<WavelengthSpectrumViewModel>();
        services.AddTransient<MonteCarloViewModel>();
        services.AddTransient<AnalysisOutputPanelViewModel>();

        // Selection-driven component property editors (right panel). Order matters:
        // most specific first, generic fallback last. ComponentEditorFactory walks them
        // and returns the first non-null editor for the selected component.
        services.AddSingleton<OnaAnalyzerEditorProvider>();
        services.AddSingleton<IComponentEditorProvider>(
            sp => sp.GetRequiredService<OnaAnalyzerEditorProvider>());
        services.AddSingleton<IComponentEditorProvider, LightSourceEditorProvider>();
        services.AddSingleton<IComponentEditorProvider, ParametricParametersEditorProvider>();
        services.AddSingleton<IComponentEditorProvider, SliderEditorProvider>();
        services.AddSingleton<IComponentEditorProvider, GenericComponentEditorProvider>();
        services.AddSingleton<ComponentEditorFactory>();

        // Bottom panel sub-ViewModels
        services.AddTransient<ConnectionRoutingViewModel>();
        services.AddTransient<ElementLockViewModel>();
        services.AddTransient<ErrorConsoleViewModel>();

        // Panel host singletons
        services.AddSingleton<LeftPanelViewModel>();
        services.AddSingleton<RightPanelViewModel>();
        services.AddSingleton<AnalysisDockViewModel>();
        services.AddSingleton<BottomPanelViewModel>();

        return services;
    }
}
