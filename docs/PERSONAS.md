# Lunima User Personas

This file is the reference for **who Lunima is built for**. Agents redesigning UX, prioritizing features, writing copy, or making workflow trade-offs must read this first and justify design decisions against these personas.

When personas conflict, the priority order for product decisions is: **Peter ≥ Mirko > Priya > Ingrid** — but no change may make the tool *incomprehensible* to Ingrid (the investor demo path must stay obvious).

---

## Persona 1: Peter — The Precision Layout Engineer

**Role:** Photonic engineer at a photonic-CPU company.
**Background:** Computer science + physics. Expert-level Nazca user. Loves cats, works long hours, very communicative — he will file detailed feedback and expects the tool to keep up with him.

### What he wants

- Edit GDS layouts **like Figma**: direct manipulation, immediate visual feedback, pixel-perfect (here: nanometer-perfect) control.
- Set **curves and pin-to-pin connection types precisely** — bend radius, curve type (Euler / arc / Bezier), waveguide width transitions — not accept whatever an autorouter picks.
- Fast, fluid canvas interactions: snapping, alignment guides, numeric input for exact values, keyboard shortcuts, undo that never lies.

### What frustrates him

- Modal dialogs interrupting flow; anything that requires more clicks than Figma would.
- Autorouting that silently overrides his manual choices.
- Imprecise or hidden geometry (rounded display values that don't match the exported GDS).

### Design implications

- Every connection/curve property must be **inspectable and editable** in a properties panel with exact numeric values.
- Direct manipulation first, dialogs last. Prefer in-canvas handles and inline editors.
- Manual edits are sacred: the system may *suggest*, never *overwrite*.
- Expose Nazca-level concepts (he knows them by name) rather than dumbing them down.

---

## Persona 2: Priya — The Academic Lab Researcher *(working name)*

**Role:** Researcher at a Princeton photonics lab with **its own fab**.
**Background:** Uses **GDSFactory**. Doesn't care about official foundry tape-outs — the lab fabricates in-house, iterates fast, and measures everything themselves.

### What she wants

- **Import her lab's own PDK** into Lunima with minimal friction and "play around": place components, test circuits, iterate.
- Run occasional FDTD — but typically via **Tidy3D** (cloud FDTD), because the lab has no big compute cluster.
- After in-house tape-out: **measure real devices and feed measurement results back into Lunima**, so simulated and measured behavior can be compared and the component models improve over time.

### What frustrates her

- PDK import that assumes a commercial foundry workflow (DRC sign-off, official tape-out gates).
- Simulation results that live in a silo — no way to attach measured S-parameters / spectra back to a component.
- GDSFactory ↔ Lunima friction (naming, layer maps, port conventions).

### Design implications

- The custom-PDK import path is a **first-class flow**, not an expert backdoor. Sensible defaults, forgiving validation.
- Support a **measurement-feedback loop**: attach measured data to a component/PDK entry and visualize measured vs. simulated.
- Interop with GDSFactory conventions where cheap (port naming, layer maps).
- Tidy3D integration matters to her mainly as a *consumer*: submit, wait, get S-matrix back.

---

## Persona 3: Mirko — The Full-Stack System Simulator

**Role:** Physicist building toward his own photonic CPU.
**Background:** Owns an Ansys Lumerical license but **prefers open-source tools**. Uses **Tidy3D** for FDTD-derived S-matrices. Deeply skeptical of black boxes — he has written **his own Python autorouting scripts** because he doesn't trust built-in autorouting.

### What he wants

- Simulate the chip **on all levels**: component FDTD → S-matrix → circuit → multi-chiplet system.
- **Export netlists** in formats other tools can consume.
- A **photonic intermediate representation (IR)**: one artifact that can hold S-matrices, netlists, FDTD results, and possibly PDK data — portable, inspectable, scriptable.
- Better/deeper **Tidy3D integration** for generating S-matrices.
- **End goal:** co-simulate a PIC together with a simulated electronic FPGA, across multiple chiplets — a full photonic-CPU system simulation with every intermediate step verifiable.

### What frustrates him

- Anything he can't verify, script, or replace. Autorouting he can't bypass or audit is a dealbreaker.
- Closed or lossy file formats; results he can't export.
- Being forced into proprietary tooling when an open-source path exists.

### Design implications

- Every pipeline stage must have an **escape hatch**: import/export at each level (geometry, S-matrix, netlist, results).
- Autorouting must be **optional and overridable**, with a documented interface so his own Python routing can plug in.
- File formats: open, documented, diff-able (JSON/text where feasible). The PDK JSON format (see `docs/PDK_JSON_FORMAT.md`) is a step in this direction.
- Netlist export is a product feature, not a debug tool.

---

## Persona 4: Ingrid — The Investor *(working name, secondary persona)*

**Role:** Potential investor evaluating Lunima.
**Background:** Limited physics/photonics understanding. Will spend **minutes, not hours** in the tool — usually watching a demo.

### What she wants

- To grasp **quickly and visually** why this tool is useful and what problem it solves.
- A demo path that "just works": open example → see a circuit → run a simulation → see a compelling, understandable result.

### Design implications

- Ship **polished example projects** that open and simulate without setup.
- First-run experience and empty states must communicate value, not assume expertise.
- Visualizations should be legible to a non-physicist at a glance (clear legends, plain-language labels alongside technical ones).
- Never *block* expert workflows for her sake — she is a constraint on the happy path's clarity, not a driver of feature depth.

---

## How agents should use this file

1. **Before UX redesigns:** state which persona(s) the change serves and check it against the "frustrates" lists of the others.
2. **Feature trade-offs:** precision & scriptability (Peter, Mirko) beat convenience shortcuts; convenience (Priya) beats demo polish (Ingrid); demo polish still matters on the first-run path.
3. **New integrations:** Tidy3D and open formats serve two personas (Priya, Mirko) — prefer them over single-persona integrations.
4. **When a persona is missing context** (e.g., a niche workflow none of them covers), say so explicitly in the issue/PR instead of inventing a fifth persona.
