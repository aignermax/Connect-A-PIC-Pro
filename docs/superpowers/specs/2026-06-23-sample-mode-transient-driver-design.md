# Sample-Mode Transient Driver + Signal-Source Library — Design

**Issue:** #600 · **Epic:** #599 (INTERCONNECT-parity transient) · **Roadmap:** #537 Pillar D
**Status:** Design pass (authored autonomously 2026-06-23). Decisions below are *proposed with rationale*; items marked ⚠️ should be validated at implementation/first-light, not taken as settled physics.

---

## 1. Problem & current state

The transient engine (#527) works like this:

- `ImpulseResponseBuilder.Build(centerNm, spanNm, nPoints)` sweeps the system S-matrix over a wavelength grid and IFFTs each (input-pin → output-pin) transfer function into a complex **impulse response** (FIR taps), `ImpulseResponse{ InputPinId, OutputPinId, Samples[] }`.
- `TimeDomainSimulator.Run` convolves each input signal with the matching IR, takes intensity `|y|²`, sums contributions per output pin → `TimeDomainResult` (per-pin trace on a shared time axis).
- `TimeSignalDefinition.FromWavelengthSweep` sets the sample rate to the **optical bandwidth** of the sweep: `dt = 1 / (fMax − fMin)` over the wavelength span.

### The core blocker

The time grid is pinned to the **optical sweep bandwidth**, not to the **modulation bandwidth of a data signal**.

For a 100 nm span around 1550 nm, `fMax − fMin ≈ 12.5 THz`, so `dt ≈ 80 fs` and `N = 256` samples covers **≈ 20 ps total**. A single bit at 25 Gbps is **40 ps**. **The current parameterization physically cannot represent a data waveform** — it can only show a femtosecond-scale impulse smear. The input is also hard-coded to one Gaussian pulse (`TimeDomainViewModel.BuildInputSignals`).

So "transient like INTERCONNECT" needs two coupled changes:
1. A **signal-driven time grid** (ns-scale, set by bitrate × samples-per-symbol × symbol count), with the impulse responses built **consistently** on that same grid.
2. A **signal-source library** producing real data waveforms (CW / pulse / PRBS-NRZ) on that grid.
3. A **stepped driver** structured so active components (#529) can later be coupled in per sample (today's batch convolution has no seam for stateful/feedback elements).

---

## 2. Goals / non-goals

**Goals**
- Configurable signal sources: CW, Gaussian pulse (keep existing), PRBS/NRZ bitstream.
- A signal-driven sampling policy that makes a data waveform representable and the IR consistent with it.
- A sample-mode driver that exposes a clean per-step seam for future active models, and that **reproduces #527's result for passive linear circuits** within tolerance.

**Non-goals (explicit, to keep #600 bounded)**
- Active component models — #529 (this issue only defines and proves the seam).
- Electrical-domain plumbing — #519 (sources here are optical; the source abstraction is domain-aware so #519 extends it).
- Waveform/eye plotting — #601 / #535.
- Coded/FEC, Monte-Carlo, electro-thermal — out of product scope (#537).

---

## 3. Design decisions

### D1 — Baseband-equivalent envelope propagation on a signal-driven grid ⚠️

Propagate the **complex envelope** of the optical field around the carrier (the optical carrier itself is implicit), sampled on a grid set by the **signal**, not the optical sweep:

- `sampleRate = bitrate × samplesPerSymbol` (e.g. 25 Gbps × 32 = 800 GSa/s → dt = 1.25 ps).
- `NSamples = samplesPerSymbol × symbolCount` (+ a settle/guard tail ≥ the IR length).
- Build the impulse responses over the **signal band around the carrier** (a span corresponding to the signal bandwidth, e.g. a few × bitrate), so the IR tap spacing equals the signal `dt`. This captures the physically relevant effects at this scale — **group delay** and (later) **dispersion** — without the femtosecond grid.

**Rationale:** envelope/baseband-equivalent modelling is the standard way circuit/link simulators handle modulated optical signals; it decouples the (irrelevant, GHz-vs-THz) carrier from the modulation. **⚠️ Validate** that resampling/rebuilding the IR onto the signal grid preserves the transfer magnitude/phase at the carrier (a flat passive splitter must still give the right split ratio and delay). Provide a fallback: if a circuit has structure faster than the signal grid resolves (e.g. a high-finesse cavity with ring-down ≫ chosen window), warn rather than silently alias.

**Alternative considered:** keep the optical-bandwidth grid and upsample the data signal to fs spacing. Rejected — N would explode (millions of samples for a few bits) for no physical gain.

### D2 — `ISignalSource` abstraction

```
public interface ISignalSource
{
    // Produces the complex-envelope (or real-drive) samples on the given grid.
    double[] Generate(TimeSignalDefinition grid);   // amplitude samples
    SignalDomain Domain { get; }                     // Optical (now) | Electrical (#519)
}
```

Implementations:
- `CwSource(amplitude)` — constant envelope.
- `PulseSource(centerPs, sigmaPs, amplitude)` — wraps the existing Gaussian (back-compat).
- `PrbsSource(bitrateGbps, prbsOrder, samplesPerSymbol, highLevel, extinctionRatioDb, seed)` — deterministic LFSR PRBS, NRZ-mapped, optional rise/fall shaping (raised-cosine) to bandlimit and avoid aliasing. Deterministic `seed` for reproducible tests.

The grid (`TimeSignalDefinition`) is derived from the **most demanding** source (highest bitrate) per D4.

### D3 — Sample-mode driver with an active-model seam

Restructure `TimeDomainSimulator.Run`'s batch convolution into a driver that advances the network and can interleave stateful elements:

```
public interface ITimeSteppable        // implemented by active models in #529
{
    // Given inputs arriving this step and internal state, produce outputs this step.
    void Step(ReadOnlySpan<Complex> inputs, Span<Complex> outputs, double dt);
}
```

- Passive links remain FIR filters (the #527 impulse responses), now evaluated as **stateful FIR filters** (per-link delay line) advanced sample-by-sample (or block-by-block with identical results).
- Active components (#529) implement `ITimeSteppable` and are scheduled into the same loop.
- **For a purely passive network the stepped driver MUST equal the #527 batch convolution** within tolerance — this is the regression guard (see §5). #600 ships passive-only; it does not implement any `ITimeSteppable`, it only defines the seam and proves equivalence.

**Rationale:** this is the minimal restructuring that (a) keeps the proven passive physics intact and (b) creates the coupling point #529 needs, without prematurely building the active models.

**⚠️ Open:** feedback loops (e.g. ring resonators as active-coupled, or any cycle through an active element) need either a delay-based decoupling (one-sample delay per cycle, INTERCONNECT-style) or an iterative solve per step. Decide in #529's design; #600 should at least not preclude it (keep the step interface delay-friendly).

### D4 — Sampling policy

- `samplesPerSymbol` default 32 (configurable; ≥ 16 enforced for anti-aliasing headroom).
- `sampleRate = bitrate × samplesPerSymbol`.
- `NSamples = samplesPerSymbol × symbolCount + guard`, where `guard ≥ IR length` so the convolution tail isn't truncated (today `Run` trims to `NSamples`, which can cut the tail — fix as part of this work).
- PRBS sources bandlimited (raised-cosine edges) to < `sampleRate/2`.

### D5 — Data model / wiring

- Sources attach to **input pins** (by pin Guid), replacing the hard-coded Gaussian in `TimeDomainViewModel.BuildInputSignals`.
- `SignalDomain.Optical` now; `Electrical` reserved for #519 (so a modulator can take an electrical PRBS drive without reworking this abstraction).

---

## 4. Architecture (vertical slice)

```
Connect-A-Pic-Core/LightCalculation/TimeDomainSimulation/
  Sources/
    ISignalSource.cs           (+ SignalDomain enum)
    CwSource.cs
    PulseSource.cs
    PrbsSource.cs              (LFSR + NRZ map + raised-cosine shaping)
  Sampling/
    SamplingPolicy.cs          (bitrate/sps/symbols → TimeSignalDefinition; guard sizing)
  ITimeSteppable.cs            (active-model seam)
  SampleModeTransientDriver.cs (stepped FIR-with-state; passive today)
CAP.Avalonia/ViewModels/Analysis/
  (extend TimeDomainViewModel: source selection + params; no plotting here — see #601)
UnitTests/LightCalculation/TimeDomainSimulation/
  PrbsSourceTests.cs · SamplingPolicyTests.cs · SampleModeDriverEquivalenceTests.cs
```

Each new file is small and single-responsibility (well under the 250-line limit). The driver extends — does not replace — the #527 building blocks (`ImpulseResponseBuilder`, `TimeDomainConvolver` semantics).

---

## 5. Testing strategy

1. **Passive equivalence (the keystone guard):** for a representative passive circuit (straight WG; directional coupler), driving with the *same* input on the *same* grid, `SampleModeTransientDriver` output must equal `TimeDomainSimulator.Run` (#527) within a tight tolerance. This proves the restructuring didn't change the physics.
2. **PRBS correctness:** LFSR sequence length `2^order − 1`, mark/space balance, determinism by seed, NRZ levels honour extinction ratio.
3. **Sampling policy:** `bitrate × sps → sampleRate`; NSamples covers `symbolCount` symbols + guard ≥ IR length; min samples/symbol enforced.
4. **Anti-aliasing:** a band-limited PRBS has negligible spectral energy above `sampleRate/2` (sanity FFT check).
5. **Energy/physical sanity:** a lossless splitter conserves summed output intensity for a CW input (carries over the #536 reflection-audit discipline).
6. **Tail integrity:** output is not truncated before the IR has decayed (regression for the current `TrimToLength`).

---

## 6. Open questions for implementation / human input

- **⚠️ D1 validation:** confirm the signal-band IR reconstruction reproduces carrier-frequency magnitude/phase; define the "structure faster than the window" warning threshold.
- **Dispersion:** group-velocity dispersion over the signal band ties into the material dispersion model (#532, done) and the process model (#570). For #600, group **delay** is enough; full dispersion can be a follow-up.
- **Feedback/cycles:** see D3 ⚠️ — settle the decoupling strategy with #529.
- **IIR fitting:** INTERCONNECT fits S-params to IIR filters for efficiency on long impulse responses. Not needed for first light (FIR is fine for short responses); note as a future optimization if IR lengths blow up.

---

## 7. Where this fits

`#527` (engine, done) → **`#600` this issue** (sources + signal-driven grid + stepped driver) → `#529` (active models on the seam) + `#519` (electrical drive) → `#601` (waveform plot) → `#535` (eye + BER). Definition of done for the epic (#599): place CW → modulator → network → photodiode, drive a 25 Gbps PRBS, see an eye diagram + BER, entirely in Lunima.
