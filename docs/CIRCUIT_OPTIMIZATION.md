# Circuit Optimization Search (Issue #820)

Netlist in → top-N improved variants out. A seeded, budget-limited local search
over the tunable parameters of the circuit on the canvas, with one-click,
undo-safe apply of any found variant.

## Search space

The search space is defined by `OptimizationParameter`
(`Connect-A-Pic-Core/Analysis/CircuitOptimization/OptimizationParameter.cs`):

- One parameter = one **component slider** (component reference + slider index).
- Bounds (`MinValue`/`MaxValue`) are copied from the slider itself, so the
  search can never leave the physically valid range of the component model.
- `Clamp(value)` enforces those bounds after every mutation.

v1 exposes slider 0 of every component that has sliders (directional-coupler
coupling ratios, phase shifters, …). The abstraction deliberately does not
assume "coupler": any future sliderable component joins the search space for
free. Topology changes (adding/swapping components) are explicitly out of
scope for v1.

## Metric definition (objective)

Objectives implement `IOptimizationObjective`:

```csharp
double Score(IReadOnlyDictionary<Guid, double> outputPowers);
```

The input is the pin-power map produced from the existing S-matrix analysis
(`ILightCalculator.CalculateFieldPropagationAsync`, power = |field|²).
**Higher score is always better** — minimization objectives negate internally,
so the search core never branches on direction.

Provided objectives:

| Objective | Meaning |
|---|---|
| `PinPowerObjective(pins, name, maximize)` | Sum of optical power over a chosen set of pins. A single pin models "power at OUT2"; the set of all listen-only coupler pins models "total transmission". |
| `TotalPowerObjective(name, maximize)` | Sum over *all* pins (e.g. minimize total stray power). |

The UI builds targets automatically: every light pin of a grating/edge coupler
becomes a selectable target, and all **laser-off** (listen-only, #690) coupler
pins together form the default "total power at outputs" target. A
"maximize/minimize" toggle covers the loss-minimization case. New metrics
(e.g. extinction ratio, balance between two pins) are added by implementing
the interface — no changes to the optimizer required.

## Search algorithm (v1: hill-climb)

`CircuitOptimizer.RunAsync(settings, cancellationToken, progress)`:

1. Save all original slider values (restored in `finally` — the canvas is
   never left in a mutated state, even on exception or cancellation).
2. Evaluate the **baseline** (current canvas values) — costs 1 evaluation.
3. Loop until the evaluation budget is exhausted:
   - Pick one parameter at random (seeded `Random`).
   - Perturb it by `delta = range · stepFraction · U(−1, 1)`, clamped to bounds.
   - Evaluate. If the score improves on the best-so-far: accept and reset the
     step size; otherwise revert and decay the step.
4. Return the top-N candidates that are **strictly better than the baseline**,
   sorted best-first and de-duplicated.

Constants (in `OptimizationSettings`): initial step fraction 0.25, decay 0.97,
minimum step fraction 0.01, default top-N 5.

Properties:

- **Deterministic**: a fixed seed reproduces the identical variant list
  (covered by `RunAsync_IsDeterministicForFixedSeed`).
- **Budget-limited**: exactly `EvaluationBudget` simulator calls, never more
  (covered by `RunAsync_RespectsEvaluationBudget`).
- **Cleanly cancellable**: cancellation returns a partial result
  (`WasCancelled = true`) with everything found so far; sliders are restored.
- **Honest**: a flat objective yields an empty variant list, not noise.

## Applying a variant

`ApplyOptimizationVariantCommand` implements `IUndoableCommand` and runs
through the global `CommandManager`, so applying a variant is a single
undo step (Ctrl+Z restores the previous slider values). Applying writes
`ComponentViewModel.SliderValue`, which triggers the normal canvas refresh
and re-simulation path.

## Non-goals (v1) / future work

- Topology synthesis (component insertion/swap) — larger search-space model.
- Robust optimization over fabrication tolerances (relates to #818).
- Smarter search (CMA-ES, Bayesian optimization) — drop-in behind the same
  `OptimizationSettings`/`OptimizationResult` contract.
- Multi-wavelength objectives (currently evaluates at 1550 nm, matching the
  parameter sweep).
