namespace CAP_DataAccess.Persistence.PIR;

/// <summary>
/// Per-instance Nazca function parameter override for a single canvas component.
/// All override fields are optional; a null value means "use the PDK template value".
/// Persisted in the .lun file under <c>NazcaOverrides[componentIdentifier]</c>.
/// Template fields capture the original PDK values at override-creation time so
/// "Reset to template" works correctly even after the component's live values have
/// already been replaced by the override.
/// </summary>
public class NazcaCodeOverride
{
    /// <summary>
    /// Override for the Nazca function name (e.g., "ebeam_mmi1x2_te1550").
    /// Null means use the PDK template's function name.
    /// </summary>
    public string? FunctionName { get; set; }

    /// <summary>
    /// Override for the Nazca function parameters string (e.g., "length=3.5,width=2.0").
    /// Null means use the PDK template's parameter string.
    /// </summary>
    public string? FunctionParameters { get; set; }

    /// <summary>
    /// Override for the Nazca module name.
    /// Null means use the PDK template's module name.
    /// </summary>
    public string? ModuleName { get; set; }

    /// <summary>
    /// PDK template's original function name, captured at override-creation time.
    /// Used by the "Reset to template" command to restore the correct value even
    /// after the live component's <c>NazcaFunctionName</c> has already been overwritten.
    /// </summary>
    public string? TemplateFunctionName { get; set; }

    /// <summary>
    /// PDK template's original function parameters, captured at override-creation time.
    /// Used by the "Reset to template" command.
    /// </summary>
    public string? TemplateFunctionParameters { get; set; }

    /// <summary>
    /// PDK template's original module name, captured at override-creation time.
    /// Used by the "Reset to template" command.
    /// </summary>
    public string? TemplateModuleName { get; set; }

    /// <summary>
    /// Complete editable Nazca cell code for this instance (issue #556). When set,
    /// the geometry preview / size recompute is driven by executing this code rather
    /// than by calling the PDK template's <see cref="FunctionName"/>.
    /// Null means no raw-code override is active (parameter-only or PDK template).
    /// When applied, the override's ports replace the template pins
    /// (see <see cref="OverridePins"/> and <see cref="HasNoSimulationModel"/>).
    /// </summary>
    public string? RawCode { get; set; }

    /// <summary>
    /// Layout backend the <see cref="RawCode"/> was written for (issue #637).
    /// One of the <see cref="OverrideBackend"/> constants; null means Nazca
    /// (the pre-#637 default), so existing overrides keep working unchanged.
    /// The matching preview service and exporter honor this value: Nazca
    /// overrides drive the Nazca export, gdsfactory overrides the gdsfactory export.
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// Component width (µm) recomputed from the rendered raw-code geometry's bounding box.
    /// Null when no raw-code override has recomputed the size.
    /// </summary>
    public double? OverrideWidthMicrometers { get; set; }

    /// <summary>
    /// Component height (µm) recomputed from the rendered raw-code geometry's bounding box.
    /// Null when no raw-code override has recomputed the size.
    /// </summary>
    public double? OverrideHeightMicrometers { get; set; }

    /// <summary>
    /// Physical pins derived from the override's rendered geometry (issue #561).
    /// When non-null and non-empty, the component's <c>PhysicalPins</c> list is replaced
    /// with these values on Apply and on project load.
    /// Null means no pin override is active (template pins remain).
    /// </summary>
    public List<OverridePinData>? OverridePins { get; set; }

    /// <summary>
    /// Snapshot of the PDK-template physical pins captured on the first Apply,
    /// before the override pins replaced them. Used by "Reset to template" to
    /// restore the original port layout without a PDK re-query.
    /// Null when no Apply has been performed yet.
    /// </summary>
    public List<OverridePinData>? TemplatePins { get; set; }

    /// <summary>
    /// True when the override's pin layout differs from the PDK template's, meaning
    /// the template S-matrix no longer maps to the new ports.
    /// When true the component has no valid simulation model — the S-matrix shown
    /// is the old template's and must not be trusted.
    /// The user should supply a matching S-matrix via the per-instance import (#541)
    /// or accept geometry/export-only mode.
    /// </summary>
    public bool HasNoSimulationModel { get; set; }

    /// <summary>
    /// Left edge of the rendered raw-code geometry's bounding box in CELL-INTERNAL
    /// Nazca coordinates (µm). The in-app pins/size are bbox-relative, so the GDS
    /// export must anchor the cell's origin at <c>PhysicalX − XMin</c> for the
    /// geometry to land on the component's grid rectangle. Null for overrides saved
    /// before this field existed — the export then falls back to default anchoring.
    /// </summary>
    public double? OverrideBboxXMinMicrometers { get; set; }

    /// <summary>
    /// Top edge (Nazca Y-up maximum) of the rendered raw-code geometry's bounding
    /// box in cell-internal Nazca coordinates (µm). Counterpart of
    /// <see cref="OverrideBboxXMinMicrometers"/> for the Y axis.
    /// </summary>
    public double? OverrideBboxYMaxMicrometers { get; set; }

    /// <summary>
    /// Records the bbox-derived geometry of an applied raw-code override in one step:
    /// component size plus the cell-internal bbox anchor the GDS export needs.
    /// </summary>
    public void SetOverrideGeometry(double width, double height, double bboxXMin, double bboxYMax)
    {
        OverrideWidthMicrometers = width;
        OverrideHeightMicrometers = height;
        OverrideBboxXMinMicrometers = bboxXMin;
        OverrideBboxYMaxMicrometers = bboxYMax;
    }

    /// <summary>
    /// Clears every raw-code-override field ("Reset to template"): code, derived
    /// geometry, pin override and the no-simulation-model flag. Parameter-override
    /// fields (<see cref="FunctionName"/> etc.) are left untouched.
    /// </summary>
    public void ClearRawCodeOverride()
    {
        RawCode = null;
        Backend = null;
        OverrideWidthMicrometers = null;
        OverrideHeightMicrometers = null;
        OverrideBboxXMinMicrometers = null;
        OverrideBboxYMaxMicrometers = null;
        OverridePins = null;
        TemplatePins = null;
        HasNoSimulationModel = false;
    }

    /// <summary>
    /// Creates an independent deep copy of this override, including new pin-list
    /// instances. Used when duplicating a component (copy/paste) so the copy's
    /// override can be edited without mutating the original component's override.
    /// </summary>
    public NazcaCodeOverride Clone() => new()
    {
        FunctionName = FunctionName,
        FunctionParameters = FunctionParameters,
        ModuleName = ModuleName,
        TemplateFunctionName = TemplateFunctionName,
        TemplateFunctionParameters = TemplateFunctionParameters,
        TemplateModuleName = TemplateModuleName,
        RawCode = RawCode,
        Backend = Backend,
        OverrideWidthMicrometers = OverrideWidthMicrometers,
        OverrideHeightMicrometers = OverrideHeightMicrometers,
        OverridePins = OverridePins?.Select(p => p.Clone()).ToList(),
        TemplatePins = TemplatePins?.Select(p => p.Clone()).ToList(),
        HasNoSimulationModel = HasNoSimulationModel,
        OverrideBboxXMinMicrometers = OverrideBboxXMinMicrometers,
        OverrideBboxYMaxMicrometers = OverrideBboxYMaxMicrometers,
    };
}
