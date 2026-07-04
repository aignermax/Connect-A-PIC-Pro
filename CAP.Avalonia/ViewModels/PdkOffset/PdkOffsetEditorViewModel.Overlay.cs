using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.PdkOffset;

/// <summary>
/// PdkOffsetEditorViewModel partial — Nazca render orchestration, canvas-pixel
/// transforms for the overlay, and the static module/function resolver shared
/// between single-component renders and the batch loop.
/// </summary>
public partial class PdkOffsetEditorViewModel
{
    private void RefreshCanvasMarkers(PdkComponentDraft draft)
    {
        CanvasComponentWidth = ComponentWidth * CanvasScale;
        CanvasComponentHeight = ComponentHeight * CanvasScale;

        if (HasNazcaOverlay)
        {
            // Nazca geometry is fixed; move Lunima box as offset changes.
            // OffsetY is the bbox TOP edge above the org (mapper convention),
            // so the box top sits OffsetY above the origin in Nazca space.
            CanvasComponentLeft = _nazcaCanvasRefX - OffsetX * CanvasScale;
            CanvasComponentTop = _nazcaCanvasRefY - OffsetY * CanvasScale;
            CanvasOriginX = _nazcaCanvasRefX;
            CanvasOriginY = _nazcaCanvasRefY;
        }
        else
        {
            CanvasComponentLeft = CanvasPadding;
            CanvasComponentTop = CanvasPadding;
            CanvasOriginX = CanvasPadding + OffsetX * CanvasScale;
            CanvasOriginY = CanvasPadding + OffsetY * CanvasScale;
        }

        PinMarkers.Clear();
        foreach (var pin in draft.Pins)
        {
            PinMarkers.Add(new PinMarker(
                pin.Name,
                CanvasComponentLeft + pin.OffsetXMicrometers * CanvasScale,
                CanvasComponentTop + pin.OffsetYMicrometers * CanvasScale));
        }
    }

    /// <summary>
    /// Applies a Nazca preview result, transforming coordinates to canvas space
    /// and populating the overlay collections.
    /// </summary>
    private void SetNazcaOverlay(NazcaPreviewResult result)
    {
        // Nazca origin is at (0,0) in Nazca space; map to canvas
        _nazcaCanvasRefX = CanvasPadding + (-result.XMin) * CanvasScale;
        _nazcaCanvasRefY = CanvasPadding + result.YMax * CanvasScale;
        // Track the Nazca bbox right/bottom so the total canvas size grows to
        // fit polygons that extend past the Lunima JSON's WidthMicrometers.
        _nazcaCanvasRight = _nazcaCanvasRefX + result.XMax * CanvasScale;
        _nazcaCanvasBottom = _nazcaCanvasRefY - result.YMin * CanvasScale;
        OnPropertyChanged(nameof(CanvasTotalWidth));
        OnPropertyChanged(nameof(CanvasTotalHeight));

        NazcaPolygons.Clear();
        foreach (var poly in result.Polygons)
        {
            var canvasPts = poly.Vertices
                .Select(v => (
                    X: _nazcaCanvasRefX + v.X * CanvasScale,
                    Y: _nazcaCanvasRefY - v.Y * CanvasScale))
                .ToList();
            NazcaPolygons.Add(new NazcaPolygonMarker(poly.Layer, canvasPts));
        }

        NazcaPinStubs.Clear();
        foreach (var pin in result.Pins)
        {
            var x0 = _nazcaCanvasRefX + pin.X * CanvasScale;
            var y0 = _nazcaCanvasRefY - pin.Y * CanvasScale;
            var x1 = _nazcaCanvasRefX + pin.StubX1 * CanvasScale;
            var y1 = _nazcaCanvasRefY - pin.StubY1 * CanvasScale;
            NazcaPinStubs.Add(new NazcaStubMarker(pin.Name, x0, y0, x1, y1));
        }

        HasNazcaOverlay = true;
        if (SelectedComponent != null)
            RefreshCanvasMarkers(SelectedComponent.Draft);
    }

    /// <summary>Triggers an async Nazca render for the given component draft.</summary>
    private async Task TriggerNazcaRenderAsync(PdkComponentDraft draft)
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        // Capture the draft this render was started for so a fast user-click that
        // changes SelectedComponent mid-flight cannot stamp our overlay on top of
        // a newer component's offsets.
        var draftAtStart = draft;

        IsNazcaRendering = true;
        NazcaOverlayStatus = "Rendering Nazca GDS preview…";

        try
        {
            var (module, function) = ResolveModuleAndFunction(draft.NazcaFunction);
            var result = await _previewService!.RenderAsync(
                module,
                function,
                draft.NazcaParameters,
                token);

            if (token.IsCancellationRequested) return;
            // SelectedComponent has moved on while we were waiting — drop result.
            if (SelectedComponent?.Draft != draftAtStart) return;

            // RenderAsync returns on a thread-pool thread; ObservableCollection
            // mutations downstream must happen on the UI thread.
            await UiThreadMarshaller(() =>
            {
                if (token.IsCancellationRequested) return;
                if (SelectedComponent?.Draft != draftAtStart) return;

                if (result.Success)
                {
                    _lastNazcaResult = result;
                    SetNazcaOverlay(result);
                    var status = $"GDS overlay loaded ({result.Polygons.Count} polygons, {result.Pins.Count} pins).";
                    if (!string.IsNullOrEmpty(result.PolygonWarning))
                        status += "  " + result.PolygonWarning;
                    NazcaOverlayStatus = status;
                    // Replace the synthetic Lunima-side snippet with the actual
                    // PDK function source pulled live by the helper script.
                    if (!string.IsNullOrEmpty(result.Source))
                        PreviewSource = result.Source;
                    ComputePinAlignment(result, draftAtStart);
                }
                else
                {
                    _lastNazcaResult = null;
                    HasNazcaOverlay = false;
                    NazcaOverlayStatus = $"Preview unavailable: {result.Error}";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection — no status update needed
        }
        catch (Exception ex)
        {
            await UiThreadMarshaller(() =>
            {
                HasNazcaOverlay = false;
                NazcaOverlayStatus = $"Preview error: {ex.Message}";
            });
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsNazcaRendering = false;
        }
    }

    /// <summary>
    /// Renders the same Python the preview helper would execute, as a string
    /// the user can read and copy. Different shape per render path:
    /// SiEPIC → klayout GDS load; demofab → Nazca cell call.
    /// </summary>
    private static string BuildPreviewSource(PdkComponentDraft draft)
    {
        var (module, function) = ResolveModuleAndFunction(draft.NazcaFunction);
        var paramsBlock = string.IsNullOrWhiteSpace(draft.NazcaParameters)
            ? "" : draft.NazcaParameters;

        if (module.StartsWith("siepic", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join("\n",
                "# SiEPIC components are read from their bundled fixed-cell GDS:",
                $"#   <{module}-package>/gds/EBeam/{function}.gds",
                "",
                "import os, klayout.db as kdb",
                $"import {module}",
                $"pkg_dir = os.path.dirname({module}.__file__)",
                $"gds = os.path.join(pkg_dir, 'gds', 'EBeam', '{function}.gds')",
                "ly = kdb.Layout(); ly.read(gds)",
                "cell = next(ly.each_cell())",
                "# polygons on layer 1/0, pins on layer 1/10");
        }

        var moduleImport = module == "demo" ? "import nazca.demofab as demo" : $"import {module} as mod";
        var modAlias = module == "demo" ? "demo" : "mod";
        var paramsRepr = string.IsNullOrEmpty(paramsBlock) ? "" : paramsBlock;
        return string.Join("\n",
            "# The preview helper builds the cell, exports a temp GDS,",
            "# and reads back polygons + pins via klayout/gdstk.",
            "import nazca as nd",
            moduleImport,
            $"cell = {modAlias}.{function}({paramsRepr})",
            "nd.export_gds(topcells=[cell], filename='preview.gds')");
    }

    /// <summary>
    /// Splits a NazcaFunction string into a Python module name and a bare
    /// function name. Two cases the PDKs use today:
    /// <list type="bullet">
    ///   <item><c>"demo.mmi2x2_dp"</c> — demofab uses dotted notation; split
    ///     at the last dot.</item>
    ///   <item><c>"ebeam_y_1550"</c> — SiEPIC EBeam exposes flat names; the
    ///     name prefix tells us which Python module owns it.</item>
    /// </list>
    /// Exposed as internal so the unit tests can lock the mapping in directly
    /// rather than going through the full async render pipeline.
    /// </summary>
    internal static (string module, string function) ResolveModuleAndFunction(string? nazcaFunction)
    {
        if (string.IsNullOrWhiteSpace(nazcaFunction))
            return ("demo", "");

        var lastDot = nazcaFunction.LastIndexOf('.');
        if (lastDot > 0)
        {
            var prefix = nazcaFunction[..lastDot];
            var fn = nazcaFunction[(lastDot + 1)..];
            // Both 'demo.foo' and 'demo_pdk.foo' (the latter appears in some
            // Lunima PDK JSONs) resolve to nazca.demofab — let the script see
            // the canonical 'demo' so it doesn't try to importlib 'demo_pdk'.
            if (prefix == "demo_pdk") prefix = "demo";
            return (prefix, fn);
        }

        // SiEPIC EBeam PDK ships flat names. Match case-insensitively because
        // SiEPIC's bundled PDK contains both ``ebeam_y_1550`` (lower) and
        // ``GC_TE_1550_8degOxide_BB`` (upper). A case-sensitive prefix list
        // would route the upper-case GC_ cells to nazca.demofab and surface
        // them as RenderFailed in the Check-All report.
        if (nazcaFunction.StartsWith("ebeam_", StringComparison.OrdinalIgnoreCase) ||
            nazcaFunction.StartsWith("gc_",    StringComparison.OrdinalIgnoreCase) ||
            nazcaFunction.StartsWith("ANT_",   StringComparison.OrdinalIgnoreCase) ||
            nazcaFunction.StartsWith("crossing_", StringComparison.OrdinalIgnoreCase) ||
            nazcaFunction.StartsWith("taper_", StringComparison.OrdinalIgnoreCase) ||
            nazcaFunction.StartsWith("contra_", StringComparison.OrdinalIgnoreCase))
        {
            return ("siepic_ebeam_pdk", nazcaFunction);
        }

        // Anything else: assume demofab, the bundled Nazca PDK.
        return ("demo", nazcaFunction);
    }
}
