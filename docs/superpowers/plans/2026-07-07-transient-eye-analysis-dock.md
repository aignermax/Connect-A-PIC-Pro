# Transient & Eye/BER Analysis Dock — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move transient simulation out of the properties panel into a first-class `SimulationMode` (toolbar CW/Transient selector) with a bottom **Analysis dock** hosting Transient + Eye/BER tabs, adopting #627's self-contained eye/BER core instead of its stale right-panel wiring.

**Architecture:** Reuse existing cores unchanged. Promote the CW/Transient flag from a view-only `TimeDomainViewModel.IsTimeDomainMode` to a shared `MainViewModel.SimulationMode`. Add an `AnalysisDockViewModel` under `BottomPanel` (mirroring `ErrorConsoleViewModel`) that owns the transient + eye sub-VMs and the collapsible/tab state. Re-point the transient view and #627's eye view onto that dock; delete the right-panel transient section.

**Tech Stack:** C# / .NET 10 / Avalonia 11 / CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`); xUnit + Shouldly + Moq; OxyPlot.

## Global Constraints

- Reuse cores verbatim — **no** transient/eye algorithm changes.
- Max 250 lines per NEW file; XML docs on public members; `_camelCase` fields, PascalCase members.
- DI registrations go in the relevant `CAP.Avalonia/DI/*Extensions.cs` method, not raw in `App.axaml.cs`.
- Tests via `python3 tools/smart_test.py <pattern>` (never `dotnet test`). Windows: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" <pattern>`.
- Adopt #627 files from branch `origin/agent/issue-535-1783014086` via `git checkout <branch> -- <path>`. Do **not** adopt #627's stale hunks: its `GdsPreviewRenderService` change in `CanvasAndPanelExtensions.cs` (main's two-backend version is newer) and its right-panel `RightPanelViewModel`/`MainWindow.axaml` eye wiring.
- Error Console dock is untouched (stays its own dock).
- Branch: `feat/transient-eye-analysis-dock` (already created; spec committed).

---

### Task 1: Adopt the Eye/BER core + its tests

**Files:**
- Create (cherry-pick from `origin/agent/issue-535-1783014086`): `Connect-A-Pic-Core/Analysis/EyeDiagram/{PrbsGenerator,EyeSimulationPlan,EyeDiagramBuilder,EyeHistogram,BerEstimator,EyeMetrics,NoiseModel}.cs`
- Test (cherry-pick): `UnitTests/Analysis/EyeDiagram/{PrbsGeneratorTests,EyeSimulationPlanTests,EyeDiagramBuilderTests,BerEstimatorTests,NoiseModelTests}.cs`

**Interfaces produced (namespace `CAP_Core.Analysis.EyeDiagram`, all BCL-only deps):**
- `enum PrbsOrder { Prbs7=7, Prbs11=11, Prbs23=23 }`; `PrbsGenerator.GenerateBits(order,bitCount)`, `.ToNrzSamples(bits,samplesPerBit,amplitude)`, `.PatternLength(order)`.
- `EyeSimulationPlan.Create(bitRateHz, sampleRateHz, patternLength)` → props `SamplesPerBit/BitCount/TotalSamples/BitPeriodSeconds`.
- `EyeDiagramBuilder.Build(trace,sampleRateHz,bitPeriodSeconds,timeBins,amplitudeBins,skipBits)` → `EyeHistogram`.
- `BerEstimator.Estimate(...)` → `record EyeMetrics(QFactor,BerEstimate,EyeHeight,EyeWidthSeconds,RmsJitterSeconds,OptimalSampleOffsetSeconds)`.
- `NoiseModel` (Gaussian receiver noise).

- [ ] **Step 1: Cherry-pick the core + tests**
```bash
git checkout origin/agent/issue-535-1783014086 -- \
  Connect-A-Pic-Core/Analysis/EyeDiagram/PrbsGenerator.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/EyeSimulationPlan.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/EyeDiagramBuilder.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/EyeHistogram.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/BerEstimator.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/EyeMetrics.cs \
  Connect-A-Pic-Core/Analysis/EyeDiagram/NoiseModel.cs \
  UnitTests/Analysis/EyeDiagram/PrbsGeneratorTests.cs \
  UnitTests/Analysis/EyeDiagram/EyeSimulationPlanTests.cs \
  UnitTests/Analysis/EyeDiagram/EyeDiagramBuilderTests.cs \
  UnitTests/Analysis/EyeDiagram/BerEstimatorTests.cs \
  UnitTests/Analysis/EyeDiagram/NoiseModelTests.cs
```

- [ ] **Step 2: Build** — `dotnet build ConnectAPICPro.sln -clp:ErrorsOnly`. Expected: 0 errors (the core is BCL-only and self-contained; a running app may lock `CAP.Desktop.dll` — that copy error is benign).

- [ ] **Step 3: Run the eye-core tests** — `py "$env:USERPROFILE\.cap-tools\smart_test.py" EyeDiagram` (also matches `Prbs`, `Ber`, `NoiseModel`). Expected: all pass. If a test references a core symbol not cherry-picked, cherry-pick that file too.

- [ ] **Step 4: Commit** — `git commit -m "(+) Adopt eye/BER analysis core from #627 (#535)"`

---

### Task 2: Promote CW/Transient to a shared `SimulationMode`

**Files:**
- Create: `CAP.Avalonia/ViewModels/Analysis/SimulationMode.cs`
- Modify: `CAP.Avalonia/ViewModels/MainViewModel.cs` (add the observable mode property near the other top-level state, ~line 56)
- Modify: `CAP.Avalonia/ViewModels/Analysis/TimeDomainViewModel.cs` (delete the view-only `_isTimeDomainMode`/`IsTimeDomainMode`, line 50 — confirmed no C# reads it)
- Test: `UnitTests/ViewModels/SimulationModeTests.cs`

**Interfaces produced:**
- `enum SimulationMode { Cw, Transient }`
- `MainViewModel.SimulationMode` (`[ObservableProperty]`, default `SimulationMode.Cw`).

- [ ] **Step 1: Create the enum**
```csharp
namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>How the design is simulated when Run (L) is invoked.</summary>
public enum SimulationMode
{
    /// <summary>Continuous-wave frequency-domain steady state (default).</summary>
    Cw,
    /// <summary>Time-domain transient: pulse response / eye-diagram basis.</summary>
    Transient,
}
```

- [ ] **Step 2: Write the failing test** (`SimulationModeTests.cs`)
```csharp
using CAP.Avalonia.ViewModels.Analysis;
using Shouldly;

namespace UnitTests.ViewModels;

public class SimulationModeTests
{
    [Fact]
    public void MainViewModel_DefaultsToCwMode()
    {
        var vm = UnitTests.Helpers.MainViewModelTestHelper.Create();
        vm.SimulationMode.ShouldBe(SimulationMode.Cw);
    }
}
```
(Use the existing `MainViewModelTestHelper` — confirm its factory method name via `UnitTests/Helpers/MainViewModelTestHelper.cs`; adjust `Create()` to the real helper API.)

- [ ] **Step 3: Run — expect FAIL** (`SimulationMode` property missing): `py "...smart_test.py" SimulationMode`.

- [ ] **Step 4: Add the property to MainViewModel**
```csharp
/// <summary>Active simulation mode; the toolbar selector binds here and Run(L) dispatches on it.</summary>
[ObservableProperty]
private CAP.Avalonia.ViewModels.Analysis.SimulationMode _simulationMode = CAP.Avalonia.ViewModels.Analysis.SimulationMode.Cw;
```

- [ ] **Step 5: Delete the dead flag** in `TimeDomainViewModel.cs` — remove the `[ObservableProperty] private bool _isTimeDomainMode = true;` (line ~50). (Its only consumers are the two radio buttons + `IsVisible` in `TimeDomainPanel.axaml`, which Task 5 removes.)

- [ ] **Step 6: Build + run test — expect PASS.**

- [ ] **Step 7: Commit** — `git commit -m "(~) Promote CW/Transient to shared MainViewModel.SimulationMode (#570)"`

---

### Task 3: Adopt TransientCircuitFactory + Eye VM; build the Analysis dock VM

**Files:**
- Create (cherry-pick): `CAP.Avalonia/ViewModels/Analysis/TransientCircuitFactory.cs`, `CAP.Avalonia/ViewModels/Analysis/EyeDiagram/EyeDiagramViewModel.cs`, `CAP.Avalonia/ViewModels/Analysis/EyeDiagram/EyeDiagramPlotBuilder.cs`
- Modify: `CAP.Avalonia/ViewModels/Analysis/TimeDomainViewModel.cs` (refactor `RunSimulationCore`'s inline circuit setup + delete private `ConfigureLightSources` → call `TransientCircuitFactory.Create(_canvas!)`, per #627's diff)
- Create: `CAP.Avalonia/ViewModels/Panels/AnalysisDockViewModel.cs`
- Modify: `CAP.Avalonia/ViewModels/Panels/BottomPanelViewModel.cs` (expose `Analysis`), `CAP.Avalonia/DI/CanvasAndPanelExtensions.cs` (register `EyeDiagramViewModel` + `AnalysisDockViewModel`)
- Test: `UnitTests/ViewModels/Panels/AnalysisDockViewModelTests.cs`

**Interfaces consumed:** `TransientCircuitFactory.Create(DesignCanvasViewModel) → (TimeDomainSimulator, PhysicalExternalPortManager)`; `TimeDomainViewModel` (ctor `(ErrorConsoleService?)`, `Configure(canvas)`, `RunTransientCommand`); `EyeDiagramViewModel` (ctor `(ErrorConsoleService?)`, `Configure(canvas)`, `RunEyeAnalysisCommand`).

**Interfaces produced:**
- `AnalysisDockViewModel(TimeDomainViewModel transient, EyeDiagramViewModel eye)` with: `TimeDomainViewModel Transient`, `EyeDiagramViewModel Eye`, `[ObservableProperty] bool IsVisible` (default false), `[ObservableProperty] int SelectedTabIndex` (0=Transient), `[RelayCommand] void Toggle()`, `void Configure(DesignCanvasViewModel canvas)` (forwards to both), `void OpenTransient()` (sets `IsVisible=true; SelectedTabIndex=0`).
- `BottomPanelViewModel.Analysis` (property).

- [ ] **Step 1: Cherry-pick the three VM files**
```bash
git checkout origin/agent/issue-535-1783014086 -- \
  CAP.Avalonia/ViewModels/Analysis/TransientCircuitFactory.cs \
  CAP.Avalonia/ViewModels/Analysis/EyeDiagram/EyeDiagramViewModel.cs \
  CAP.Avalonia/ViewModels/Analysis/EyeDiagram/EyeDiagramPlotBuilder.cs
```

- [ ] **Step 2: Refactor `TimeDomainViewModel.RunSimulationCore`** to use the factory (mirrors #627). Replace the inline `ComponentListTileManager`/`GridManager`/`SystemMatrixBuilder`/`TimeDomainSimulator` block **and** delete the private `ConfigureLightSources(...)` method (lines ~178-225), substituting:
```csharp
var (simulator, portManager) = TransientCircuitFactory.Create(_canvas!);
```
Keep the rest of `RunSimulationCore` (pulse build, `simulator.Run(...)`, result plotting) unchanged.

- [ ] **Step 3: Write the failing test** (`AnalysisDockViewModelTests.cs`)
```csharp
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;

namespace UnitTests.ViewModels.Panels;

public class AnalysisDockViewModelTests
{
    private static AnalysisDockViewModel Make() =>
        new(new TimeDomainViewModel(), new EyeDiagramViewModel());

    [Fact]
    public void StartsCollapsed_OnTransientTab()
    {
        var vm = Make();
        vm.IsVisible.ShouldBeFalse();
        vm.SelectedTabIndex.ShouldBe(0);
    }

    [Fact]
    public void Toggle_FlipsVisibility()
    {
        var vm = Make();
        vm.ToggleCommand.Execute(null);
        vm.IsVisible.ShouldBeTrue();
    }

    [Fact]
    public void OpenTransient_ShowsDockOnTransientTab()
    {
        var vm = Make();
        vm.SelectedTabIndex = 1;
        vm.OpenTransient();
        vm.IsVisible.ShouldBeTrue();
        vm.SelectedTabIndex.ShouldBe(0);
    }
}
```

- [ ] **Step 4: Run — expect FAIL** (`AnalysisDockViewModel` missing): `py "...smart_test.py" AnalysisDockViewModel`.

- [ ] **Step 5: Implement `AnalysisDockViewModel`** (mirror `ErrorConsoleViewModel`'s IsVisible/Toggle pattern)
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>Bottom analysis dock: collapsible host for the Transient and Eye/BER tabs (#570/#535).</summary>
public partial class AnalysisDockViewModel : ObservableObject
{
    /// <summary>Transient (time-domain) analysis tab.</summary>
    public TimeDomainViewModel Transient { get; }
    /// <summary>Eye-diagram / BER analysis tab.</summary>
    public EyeDiagramViewModel Eye { get; }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private int _selectedTabIndex;

    public AnalysisDockViewModel(TimeDomainViewModel transient, EyeDiagramViewModel eye)
    {
        Transient = transient;
        Eye = eye;
    }

    /// <summary>Wires both tabs to the active design canvas.</summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        Transient.Configure(canvas);
        Eye.Configure(canvas);
    }

    /// <summary>Opens the dock on the Transient tab (called when Run is invoked in Transient mode).</summary>
    public void OpenTransient()
    {
        SelectedTabIndex = 0;
        IsVisible = true;
    }

    [RelayCommand]
    private void Toggle() => IsVisible = !IsVisible;
}
```

- [ ] **Step 6: Expose on `BottomPanelViewModel`** — add `public AnalysisDockViewModel Analysis { get; }`, a ctor parameter `AnalysisDockViewModel analysis`, and assign it (mirror the existing `ErrorConsole` wiring at lines 31/44/48).

- [ ] **Step 7: DI** — in `CanvasAndPanelExtensions.cs`, add (do NOT touch the `GdsPreviewRenderService` registration): `services.AddTransient<EyeDiagramViewModel>();` (next to the `TimeDomainViewModel` line ~57) and `services.AddSingleton<AnalysisDockViewModel>();` (next to `BottomPanelViewModel`, ~line 78).

- [ ] **Step 8: Build + run test — expect PASS.**

- [ ] **Step 9: Commit** — `git commit -m "(+) Analysis dock VM + adopt TransientCircuitFactory & EyeDiagramViewModel from #627 (#535/#570)"`

---

### Task 4: Run-dispatch by mode + open dock on transient run

**Files:**
- Modify: `CAP.Avalonia/ViewModels/MainViewModel.cs` (`RunSimulation`/`ExecuteSimulation`, lines 741-796; the design canvas `Configure` at ~176 to also configure the Analysis dock)
- Test: `UnitTests/ViewModels/SimulationModeTests.cs` (extend)

**Interfaces consumed:** `MainViewModel.SimulationMode` (Task 2), `BottomPanel.Analysis` (Task 3), `BottomPanel.Analysis.Transient.RunTransientCommand`, `AnalysisDockViewModel.OpenTransient()`.

- [ ] **Step 1: Write the failing test** — Run in Transient mode opens the dock and does not toggle the CW power overlay.
```csharp
[Fact]
public async Task Run_InTransientMode_OpensAnalysisDock_NotCwOverlay()
{
    var vm = UnitTests.Helpers.MainViewModelTestHelper.Create();
    vm.SimulationMode = SimulationMode.Transient;

    await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.RunSimulationCommand).ExecuteAsync(null);

    vm.BottomPanel.Analysis.IsVisible.ShouldBeTrue();
    vm.BottomPanel.Analysis.SelectedTabIndex.ShouldBe(0);
    vm.Canvas.ShowPowerFlow.ShouldBeFalse();   // CW overlay not triggered in transient mode
}
```
(Confirm `RunSimulationCommand` is an `IAsyncRelayCommand`; adjust the cast/`ExecuteAsync` to the generated type.)

- [ ] **Step 2: Run — expect FAIL** (transient mode still runs CW path).

- [ ] **Step 3: Branch the Run entry point.** In `RunSimulation()` (line 741), before the CW toggle logic, add:
```csharp
if (SimulationMode == CAP.Avalonia.ViewModels.Analysis.SimulationMode.Transient)
{
    BottomPanel.Analysis.OpenTransient();
    await BottomPanel.Analysis.Transient.RunTransientCommand.ExecuteAsync(null);
    return;
}
```
Leave the existing CW toggle/`ExecuteSimulation` path for `Cw`.

- [ ] **Step 4: Configure the dock with the canvas.** Where the canvas is wired (`MainViewModel` ~line 176, alongside `RightPanel` config), add `BottomPanel.Analysis.Configure(_canvas);` so both tabs get the canvas. (Verify `_canvas` field name in context.)

- [ ] **Step 5: Build + run test — expect PASS.** Also run the existing CW test path if present to confirm `Cw` mode is unchanged.

- [ ] **Step 6: Add the toolbar selector** (`MainWindow.axaml`, inside the horizontal toolbar StackPanel, right after the Run button at line 82, before the `<Separator>` at 84):
```xml
<!-- Simulation mode: CW vs Transient. Run (L) executes the active mode. -->
<ComboBox Width="120" Margin="2" VerticalAlignment="Center"
          SelectedIndex="{Binding SimulationMode, Converter={x:Static conv:SimulationModeIndexConverter.Instance}}"
          ToolTip.Tip="Simulation mode: CW (steady-state) or Transient (time-domain). Run (L) executes this mode.">
    <ComboBoxItem>CW</ComboBoxItem>
    <ComboBoxItem>Transient</ComboBoxItem>
</ComboBox>
```
If a value converter is undesirable, instead bind two `RadioButton`s to `SimulationMode` via a shared enum-to-bool converter, or expose `bool IsTransientMode => SimulationMode == Transient` with a setter on MainViewModel and bind the ComboBox `SelectedIndex` to a plain `int SimulationModeIndex` property. **Simplest:** add `public int SimulationModeIndex { get => (int)SimulationMode; set => SimulationMode = (SimulationMode)value; }` to MainViewModel (raise `OnPropertyChanged` in the `SimulationMode` partial `OnSimulationModeChanged`) and bind `SelectedIndex="{Binding SimulationModeIndex}"` — no converter needed. Use this approach.

- [ ] **Step 7: Build. Commit** — `git commit -m "(+) Run dispatches by SimulationMode; toolbar CW/Transient selector (#570)"`

---

### Task 5: Views — Analysis dock panel, re-home transient + eye, remove from right panel

**Files:**
- Create: `CAP.Avalonia/Views/Panels/AnalysisDockPanel.axaml(.cs)`
- Modify: `CAP.Avalonia/Views/Panels/TimeDomainPanel.axaml` (strip mode radios/CW hint; re-point bindings)
- Adopt + re-point: `CAP.Avalonia/Views/Panels/EyeDiagramPanel.axaml(.cs)`
- Modify: `CAP.Avalonia/Views/MainWindow.axaml` (remove `<panels:TimeDomainPanel/>` at 790; add the dock at the bottom next to the Error Console dock)
- Modify: `CAP.Avalonia/ViewModels/Panels/RightPanelViewModel.cs` (remove `TimeDomain` property/ctor-param/assign/Configure at lines 114/152/169/176)

**Interfaces consumed:** `BottomPanel.Analysis.Transient.*`, `BottomPanel.Analysis.Eye.*`, `BottomPanel.Analysis.IsVisible`, `BottomPanel.Analysis.ToggleCommand`, `BottomPanel.Analysis.SelectedTabIndex`.

- [ ] **Step 1: Adopt the Eye view**
```bash
git checkout origin/agent/issue-535-1783014086 -- \
  CAP.Avalonia/Views/Panels/EyeDiagramPanel.axaml \
  CAP.Avalonia/Views/Panels/EyeDiagramPanel.axaml.cs
```

- [ ] **Step 2: Re-point `TimeDomainPanel.axaml`** — delete the "Simulation mode" label + the two `RadioButton`s (lines 22-33) and the CW hint `TextBlock` (lines 35-39). Unwrap the `IsVisible`-gated `<StackPanel IsVisible="{Binding RightPanel.TimeDomain.IsTimeDomainMode}">` (line 42) — the content is always shown inside the tab now. Change every `RightPanel.TimeDomain.` binding to `BottomPanel.Analysis.Transient.` (params, `RunTransientCommand`, `IsRunning`, `StatusText`, `HasResult`, `PlotModel`, `Series`, `ResultText`, `ExportCsvCommand`). Keep `x:DataType="vm:MainViewModel"`.

- [ ] **Step 3: Re-point `EyeDiagramPanel.axaml`** — change every `RightPanel.EyeDiagram.` binding to `BottomPanel.Analysis.Eye.` (`BitRateGbps`, `PrbsOrders`, `SelectedPrbsOrder`, `ThresholdRelative`, `RunEyeAnalysisCommand`, `IsRunning`, `StatusText`, `HasResult`, `PlotModel`, `MetricsText`, `ExportCsvCommand`). Keep `x:DataType="vm:MainViewModel"`.

- [ ] **Step 4: Create `AnalysisDockPanel.axaml`** — mirror the Error Console dock (`MainWindow.axaml:263-333`): a `Border` with a header `DockPanel` (toggle `Button` bound to `BottomPanel.Analysis.ToggleCommand`, `▶`/`▼` chevrons on `BottomPanel.Analysis.IsVisible`, label "Analysis"), and a collapsible content `Border IsVisible="{Binding BottomPanel.Analysis.IsVisible}" Height="240"` containing a `TabControl SelectedIndex="{Binding BottomPanel.Analysis.SelectedTabIndex}"` with two `TabItem`s: Header "Transient" → `<panels:TimeDomainPanel/>`; Header "Eye / BER" → `<panels:EyeDiagramPanel/>`. `x:DataType="vm:MainViewModel"`.

- [ ] **Step 5: Host the dock in `MainWindow.axaml`** — add `<panels:AnalysisDockPanel/>` at the bottom, docked above/next to the existing Error Console dock (both `DockPanel.Dock="Bottom"`; place the Analysis dock so it stacks with the console). Remove `<panels:TimeDomainPanel/>` at line 790.

- [ ] **Step 6: Remove TimeDomain from RightPanel** — in `RightPanelViewModel.cs` delete the `TimeDomain` property (114), ctor param (152), assignment (169), and `TimeDomain.Configure(canvas)` (176). `TimeDomainViewModel` stays DI-registered (now consumed by `AnalysisDockViewModel`).

- [ ] **Step 7: Build** — `dotnet build ConnectAPICPro.sln -clp:ErrorsOnly`. Fix binding/namespace errors. Expected: 0 errors (ignore a `CAP.Desktop.dll` lock from a running app).

- [ ] **Step 8: Run the app briefly** (`run` skill / `dotnet run --project CAP.Desktop`) and confirm: properties panel has no transient section; toolbar has the CW/Transient selector; switching to Transient + Run opens the bottom Analysis dock with the waveform; the Eye/BER tab renders its empty plot. Capture a screenshot if the UI-screenshot harness is available.

- [ ] **Step 9: Commit** — `git commit -m "(+) Analysis dock panel; re-home transient + eye; remove transient from properties (#570/#535)"`

---

### Task 6: Full-suite verification + supersede #627

**Files:** none (verification + housekeeping).

- [ ] **Step 1: Full suite** — `py "$env:USERPROFILE\.cap-tools\smart_test.py"`. Expected: green apart from the known parallel-load flakes (`PhotonTorchScriptExecution`, `GdsExportAlignment`, `ComponentSettingsDialogSolverStatus`); confirm any failure passes in isolation.

- [ ] **Step 2: Grep for stragglers** — no remaining `RightPanel.TimeDomain` / `RightPanel.EyeDiagram` bindings; no `IsTimeDomainMode` references. `git grep -n "RightPanel.TimeDomain\|RightPanel.EyeDiagram\|IsTimeDomainMode"` → empty.

- [ ] **Step 3: PR** — open a PR (base `main`) summarizing: transient re-homed to the Analysis dock, first-class CW/Transient mode, #627 eye/BER adopted into the dock. Note that **#627 should be closed as superseded** (its core lives on here; its right-panel wiring is intentionally dropped).

---

## Notes for the executor
- The `MainViewModelTestHelper` factory and the exact generated command types (`RunSimulationCommand`, `RunTransientCommand`) must be confirmed against the real files when writing tests — adjust casts accordingly.
- Do not re-run `git checkout <branch> -- …` for files already adopted in an earlier task.
- If a cherry-picked file fails to compile against current main (namespace/signature drift), adapt the file to main — do not roll main back.
</content>
