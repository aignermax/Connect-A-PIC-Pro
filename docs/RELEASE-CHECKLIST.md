# Release Checklist — Lunima

> Complete the list before every release. Copy it into the release issue, tick items off, and link any failing item to a follow-up issue.
>
> Verification: `auto` = covered by a named test; `manual` = needs a human with the UI. Unmarked items default to `manual`.

## Maintenance rule

Every PR that adds, removes, or materially changes a user-facing feature must update this file. Reviewers should check that the relevant checkbox is present and the expected result still matches the merged behavior.

## How to use at release time

1. Open a release issue / draft PR and paste the checklist below into it.
2. Walk through every manual item in a fresh build on each supported OS (Windows, Linux, macOS) where feasible.
3. Tick an item only after you have observed the expected result.
4. If a manual item fails, file a blocker issue and do not ship the release.
5. For `auto` items, rely on CI unless you touched the related area — then re-run the named tests locally.

---

## 1. Application startup & project lifecycle

- [ ] Start Lunima without a command-line file → Home screen appears with recent projects and example tiles. `(manual)`
- [ ] Open an example project from the Home screen → design loads and canvas is usable. `(auto: HomeExamplesTests)`
- [ ] Create a new project → an empty canvas with the chosen process / playground is shown. `(auto: FileOperationsProjectLifecycleTests)`
- [ ] Reopen the last project on startup → previous file opens automatically if preference is enabled. `(auto: HomeReopenLastProjectTests)`
- [ ] Save a design as `.lun`, close, and reopen → layout, components, and settings are restored. `(auto: FileOperationsProjectLifecycleTests)`
- [ ] **Save As** creates a new file without changing the current open path. `(manual)`
- [ ] Dirty-state marker (`*`) appears in title bar after an edit and clears on save. `(auto: WindowTitleTests)`
- [ ] Check for update banner appears when a newer release is available. `(manual)`
- [ ] Settings window opens from toolbar and shows all settings pages. `(manual)`

## 2. Localization & accessibility

- [ ] Switch UI language in Settings → all menus, tooltips, and status text update without restart. `(auto: LocalizeExtensionLiveSwitchTests)`
- [ ] Status-bar messages re-translate after a live language switch. `(auto: MainViewModelLocalizationTests)`
- [ ] Export outputs (scripts, GDS params) use invariant culture for numeric formatting. `(auto: NazcaExportCultureInvarianceTests)`

## 3. Design canvas

- [ ] Pan and zoom the canvas with mouse wheel / trackpad. `(auto: ZoomToFitTests)`
- [ ] Zoom to fit shows the whole design centered in the viewport. `(auto: ZoomToFitTests)`
- [ ] Select, drag, rotate, and delete selected components. `(auto: RotateComponentCommandTests, DeleteComponentCommandTests)`
- [ ] Box-select multiple components and move them as a group. `(auto: BoxSelectionSyncTests)`
- [ ] Undo/redo covers placement, move, rotate, delete, and route edits. `(auto: UndoRedoIntegrationTests)`
- [ ] Canvas context menu offers Edit, Copy, Paste, Group, Ungroup, Rename, Save as Prefab, Delete. `(auto: CanvasContextMenuComponentSettingsTests)`
- [ ] Grid snap settings (from Settings) affect placement and movement. `(auto: GridSnapSettingsTests)`
- [ ] Alignment guides appear when dragging a component near another. `(auto: AlignmentGuideViewModelTests)`
- [ ] Probe mode opens the mode-probe flyout at the clicked location. `(manual)`
- [ ] Cut mode / scissors tool splits a waveguide. `(manual)`
- [ ] Transient mode hides the CW power-flow overlay and shows laser on/off controls. `(auto: DesignCanvasSimulationModeActiveTests)`

## 4. Component library & PDK management

- [ ] Component library loads bundled components grouped by category. `(auto: ComponentLibraryViewModelTests)`
- [ ] Search text filters the component library. `(auto: ComponentLibraryViewModelTests)`
- [ ] PDK enable/disable toggles filter the visible components. `(auto: PdkManagerViewModelTests)`
- [ ] Drag a component from the library and drop it onto the canvas → instance is placed. `(manual)`
- [ ] Load a custom PDK JSON from the Tools flyout → new components appear in the library. `(auto: PdkLoaderTests)`
- [ ] Create a custom PDK via the left-panel `+` button → PDK file is written and selectable. `(auto: CreateCustomPdkFlowTests)`
- [ ] Delete a custom PDK and restore it from the trash panel. `(auto: PdkTrashViewModelTests)`
- [ ] Edit a PDK's process / layers / materials (forks bundled PDKs as custom). `(manual)`
- [ ] Process lock prevents mixing incompatible PDKs on the same design. `(auto: PdkManagerProcessLockTests)`
- [ ] Active process label in the status bar reflects the current design process. `(auto: MainViewModelProcessTests)`

## 5. Groups, hierarchy & reusable blocks

- [ ] Create a group from selected components with `Ctrl + G`. `(auto: GroupingWorkflowTests)`
- [ ] Ungroup a group with `Ctrl + Shift + G`. `(auto: GroupingWorkflowTests)`
- [ ] Rename a group via context menu and see the change in the Hierarchy panel. `(auto: HierarchyRenameTests)`
- [ ] Save a group as a prefab → it appears in the Saved Groups list. `(auto: SaveGroupAsPrefabCommandTests)`
- [ ] Enter group edit mode, edit internals, and exit without losing connections. `(auto: GroupEditModeTests, GroupEditModeIntegrationTests)`
- [ ] Nested groups render and simulate correctly. `(auto: ComponentGroupSMatrixBuilderTests)`
- [ ] Hierarchy panel shows the design tree and selection syncs with the canvas. `(auto: HierarchyPanelViewModelTests)`

## 6. Routing & connections

- [ ] Connect mode draws a waveguide between two pins. `(auto: ViewModels/Canvas/ConnectModeAutoSwitchTests)`
- [ ] A* routing finds a path around obstacles and respects grid ownership. `(manual)`
- [ ] Manual bend handles adjust waveguide shape. `(auto: SegmentShiftCommandTests, BendRadiusCommandTests)`
- [ ] Bend radius cannot be reduced below the active process minimum. `(auto: BendRadiusCommandTests)`
- [ ] Crossing-insertion routes around existing components automatically. `(auto: CrossingInsertionCanvasBinderTests, InsertManualCrossingCommandTests)`
- [ ] Connection style panel changes curve type, width, radius, and freeze state. `(auto: ConnectionRoutingViewModelShapeTests, ConnectionRoutingStyleEffectTests)`
- [ ] Frozen routes persist through save/load. `(auto: Serialization/LockStatePersistenceTests)`
- [ ] Metal routing (electrical traces) exports with process-defined layers and widths. `(auto: MetalRouting/ElectricalConnectionExportTests)`

## 7. Simulation & S-matrix

- [ ] Run CW simulation → optical power reaches the expected output ports. `(auto: SimulationModeTests, LightCalculation/TransitiveSMatrixCalculatorTests)`
- [ ] Run Transient simulation → time-domain output appears in the analysis dock. `(auto: TimeDomainSimulation/TimeDomainSimulatorTests)`
- [ ] Toggle simulation mode (CW / Transient) from the toolbar combo box. `(auto: SimulationModeTests)`
- [ ] Simulation converges for looped circuits or emits a readable non-convergence message. `(auto: NonConvergentCircuitMessageFormatterTests)`
- [ ] Passivity warnings surface when components produce unphysical gain. `(auto: LightCalculation/SingleHopPassivityCheckerTests)`
- [ ] S-matrix performance diagnostics show aggregate stats for the current design. `(manual)`

## 8. Parameter sweep

- [ ] Select a component with a slider, set start/end/steps, and run a parameter sweep. `(auto: ParameterSweeperTests)`
- [ ] Sweep results update and can be exported to CSV. `(auto: SweepCsvExporterTests)`
- [ ] Sweep respects slider min/max bounds from the selected template. `(auto: ParameterSweeperTests)`

## 9. Analysis panels

- [ ] ONA wavelength sweep panel measures transmission across a wavelength range. `(auto: OnaAnalysis/OnaAnalyzerSimulationTests)`
- [ ] Wavelength spectrum panel plots transmission curves. `(auto: WavelengthSpectrum/WavelengthSpectrumViewModelTests)`
- [ ] Time-domain / transient analysis panel plots signals over time. `(auto: TimeDomainSimulation/TimeDomainSimulatorTests)`
- [ ] Eye diagram panel opens and renders eye metrics / BER. `(auto: EyeDiagram/EyeDiagramBuilderTests, BerEstimatorTests)`
- [ ] Monte Carlo panel runs fabrication-variance sweeps. `(auto: MonteCarloAnalysis/MonteCarloRunnerTests)`
- [ ] Circuit optimization panel suggests and applies component variants. `(auto: CircuitOptimization/CircuitOptimizerTests)`
- [ ] Design validation panel lists and navigates to rule violations. `(auto: DesignValidatorTests, DesignValidationIntegrationTests)`
- [ ] Component dimension diagnostics detect out-of-bounds geometry. `(auto: ComponentDimensionValidatorTests)`
- [ ] Layout compression panel shrinks the design within constraints. `(auto: LayoutCompressorTests)`
- [ ] Analysis output panel shows designated outputs and persists them. `(auto: AnalysisOutput/AnalysisOutputPanelViewModelTests)`

## 10. Component-specific tools & properties

- [ ] Selected-component properties panel shows name, position, size, and type-specific editors. `(auto: Properties/SelectionPropertiesPanelIntegrationTests)`
- [ ] Light-source editor toggles input power and wavelength. `(manual)`
- [ ] ONA analyzer editor configures sweep range. `(auto: Properties/OnaAnalyzerEditor)`
- [ ] Slider editor appears for components with tuning parameters. `(auto: Properties/SliderEditor)`
- [ ] Parametric formula editors accept and evaluate user equations. `(auto: Properties/ParametricParametersEditorTests)`
- [ ] Component settings dialog opens, shows S-matrix / display / import / FDTD tabs, and saves changes. `(auto: ComponentSettingsDialogViewModelTests)`
- [ ] Port mapping dialog remaps imported S-parameter ports to internal pins. `(auto: PortMappingDialogIntegrationTests)`
- [ ] S-matrix override applies to all instances or a single instance. `(auto: SMatrixOverrideApplicatorTests)`
- [ ] Unlock all elements from the Tools flyout re-enables editing on locked items. `(manual)`

## 11. Mode solver & FDTD

- [ ] Mode solver dialog opens from Tools and runs against the configured Python environment. `(auto: Solvers/ModeSolver/ModeSolverViewModelTests)`
- [ ] Mode-probe flyout shows effective index and mode profile for a clicked waveguide. `(manual)`
- [ ] FDTD backend selection dialog lists available environments and toggles Tidy3D settings. `(manual)`
- [ ] Docker setup dialog detects or installs a Python/Nazca environment. `(manual)`
- [ ] Python environment manager installs/updates interpreters and the Nazca package. `(auto: PythonEnvironmentManager/PythonEnvironmentManagerViewModelTests)`

## 12. Import

- [ ] Import a GDS layout via the library panel `GDS` button → pins appear on the imported cell. `(auto: Services/GdsImport/GdsImportServiceTests)`
- [ ] GDS import dialog previews the cell hierarchy and lets the user pick placement. `(auto: Services/GdsImport/GdsImportDialogViewModelTests)`
- [ ] Imported components are linked to the design scope (saved in `.lun`). `(auto: Services/GdsImport/DesignScopedGdsComponentServiceTests)`
- [ ] Import a PDK JSON and register its components. `(auto: ViewModels/PdkImport/PdkImportWizardViewModelTests)`
- [ ] Round-trip: export a layout, import it back, and compare geometry. `(auto: Services/GdsImport/GdsRoundTripImportTests)`

## 13. Export

- [ ] Nazca Python export produces a runnable script with correct coordinates. `(auto: CodeExporter/SimpleNazcaExporterTests)`
- [ ] GDS export writes a valid GDS file with layers, pins, and cells. `(auto: Export/GdsExportIntegrationTests)`
- [ ] SAX export produces a SAX-compatible Python netlist/script. `(auto: Export/SaxScriptExecutionTests)`
- [ ] gdsfactory export generates a YAML netlist and Python module. `(auto: Export/GdsFactoryExport/GdsFactoryExporterTests, Export/Netlist/GdsFactoryYamlNetlistWriterTests)`
- [ ] PhotonTorch export dialog configures and writes an export script. `(auto: Export/PhotonTorchExporterTests)`
- [ ] Verilog-A export produces a SPICE-compatible model. `(auto: Export/VerilogAExporterTests, Export/VerilogASimulationTests)`
- [ ] Netlist panel displays a live-derived YAML netlist. `(auto: Export/Netlist/NetlistViewModelTests)`
- [ ] Export guards warn when the design contains broken routes or missing PDKs. `(auto: Export/GdsExportGuardTests, Export/NazcaExportSkipsBrokenConnectionsTests)`
- [ ] Foundry environment selection resolves a working Python/Nazca interpreter. `(auto: Export/GdsExportEnvironmentSelectionTests, Export/ProcessLaunchFactoryTests)`
- [ ] PDK resolution check validates all Nazca functions resolve for export. `(auto: Export/PdkResolution/PdkFunctionResolutionServiceTests)`

## 14. Layers & geometry

- [ ] Layer list is shown and configurable per PDK process. `(manual)`
- [ ] GDS layer mapping in exported files matches the active process. `(auto: Export/SimpleNazcaExporterConnectionSourceLayerTests)`
- [ ] PDK offset editor calibrates component pin offsets against a reference GDS. `(auto: PdkOffsetEditorViewModelTests)`
- [ ] Component outline polygons render and export correctly. `(auto: Controls/Canvas/ComponentPreview/GdsPolygonRendererTests)`
- [ ] GDS coordinate comparison tool reports differences between two exports. `(manual)`

## 15. AI design assistant

- [ ] AI assistant panel accepts natural-language requests. `(manual)`
- [ ] AI tools registry exposes fit-to-view and other canvas operations. `(auto: AI/FitToViewToolTests, AI/AiToolRegistryTests)`
- [ ] AI grid service applies tool results to the design model. `(auto: AI/AiGridServiceTests)`
- [ ] AI settings page configures the API key / endpoint. `(manual)`

## 16. Component registry (online)

- [ ] Open Component Registry browser from toolbar or library header. `(manual)`
- [ ] Registry browser fetches the public index and displays components. `(auto: ComponentRegistry/RegistryBrowser/RegistryActiveProcessWiringTests)`
- [ ] Download and register a registry component into the local library. `(manual)`

## 17. Error console, diagnostics & help

- [ ] Error console opens with `F12`, lists errors/warnings, and copies to clipboard. `(auto: ErrorConsoleViewModelTests)`
- [ ] Error badge counts update after validation runs. `(manual)`
- [ ] Routing diagnostics panel highlights blocked paths and detours. `(manual)`
- [ ] GDS coordinate comparison view loads two coordinate JSON exports side by side. `(manual)`
- [ ] Help flyouts for transient and eye workflows appear on first use. `(manual)`
- [ ] PDK JSON help page opens from the Tools flyout. `(manual)`

## 18. Cross-platform sanity

- [ ] All export scripts run on the named interpreter across Windows, Linux, and macOS. `(auto: Export/PythonConfigurationIntegrationTests)`
- [ ] No direct `ProcessStartInfo` / `Process.Start` calls outside sanctioned launchers. `(auto: Architecture/CrossPlatformProcessLaunchTests)`
- [ ] File paths use `Path.Combine` and OS-appropriate separators. `(manual / code review)`

## 19. Performance & stability

- [ ] Large design (> 500 components) opens without UI hangs. `(manual)`
- [ ] Memory usage remains stable after repeated open/save cycles. `(manual)`
- [ ] Crash during simulation shows a readable error in the console rather than a hard exit. `(manual)`

---

## Sign-off

- **Tester:** ___________________
- **Build / version:** ___________________
- **Date:** ___________________
- **Blockers:** ___________________
- **Approved for release:** ☐ Yes  ☐ No
