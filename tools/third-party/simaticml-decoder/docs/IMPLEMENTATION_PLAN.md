# simaticml-decoder — Implementation Plan

> **Historical snapshot (2026-06).** This handoff plan describes the original scaffold and is
> retained for auditability. Its module-status table and unchecked steps are not current. See the
> [architecture](ARCHITECTURE.md), [current capabilities](CAPABILITIES.md), and
> [roadmap](../ROADMAP.md) for present state and direction.

*Handoff brief for continuing the work in Claude Code. This document is self-contained: it assumes no prior conversation. Read it top to bottom before writing code.*

---

## 1. Mission

Build a deterministic Python tool that translates exported **SimaticML LAD/FBD** blocks (Siemens TIA Portal **V21**) into **readability-first SCL** plus a **JSON metadata sidecar**.

The point: a SimaticML export stores ladder logic as a `FlgNet` — a flat netlist of *parts* and *wires* joined by `UId`, not line-shaped rungs. Reading that graph by hand (or by eye in the editor) is slow and error-prone — it is easy to connect the wrong variable in a large block, and a contact's negation is a single diagonal slash. Decoding it deterministically turns the netlist into folded logic with **explicit** negation and an **exact** write/read cross-reference. The downstream consumer is the `plc-code-analysis` skill, which will analyse the decoded output instead of walking raw XML.

---

## 2. Current state (already in the repo)

The scaffold is built, imports clean, byte-compiles on 3.11/3.12, and ships a working `--help`. Module status:

| Module | State | Role |
|--------|-------|------|
| `model.py` | **concrete** | Typed mirror of the XML syntax (the "Stable Parser Model"). |
| `ir.py` | **concrete** | Folded-logic types: boolean expression tree + statements. |
| `instructions.py` | **concrete** | Part catalog — *data, not logic*. 29 entries seeded. |
| `operand.py` | stub | `Access` → display string. Implement with Phase 1. |
| `scl_reconstruct.py` | stub | SCL networks → text (reconstruct, not fold). |
| `parse.py` | stub | **Phase 1.** |
| `fold.py` | stub | **Phase 2.** Intended algorithm is in its docstring. |
| `emit.py` | stub | **Phase 3.** |
| `cli.py` | skeleton | One-file CLI; pipeline flow is commented, not wired. |
| `pyproject.toml` | done | hatchling; stdlib-only runtime; `dev` = ruff + pytest; entry point `simaticml-decode`. |
| `.github/workflows/ci.yml` | done | ruff + pytest matrix; tolerates "no tests yet". |
| `README.md` | done & frozen | Do not rewrite (see conventions). |
| `tests/` | empty | Authored separately by the maintainer. |

The stubs raise `NotImplementedError` with a phase tag. Fill them in the order below.

---

## 3. Repo setup before you start

Two sets of files referenced by this plan are **not yet in the repo** — add them:

1. **`docs/SIMATICML_READING_GUIDE.md`** — the authoritative, XSD-cross-checked format spec. *This is the primary reference for every parsing and folding question below.* When this plan and the guide disagree, the guide wins on format details.
2. **`tests/fixtures/`** — the six real V21 sample exports: `InvertBit.xml`, `SimpleDevice.xml`, `deviceState.xml`, `SingleAlarm_FB.xml`, `Motor.xml`, `FB_SYSTEM.xml`. These are the regression corpus (see §8).

---

## 4. Architecture (locked — do not violate)

Three cleanly separated, independently testable phases:

```
parse.py   XML       → model.*   (faithful syntactic mirror; all XML quirks live here)
fold.py    model.*   → ir.*      (semantics: boolean tree + assignments; no XML knowledge)
emit.py    ir.*      → SCL text + JSON sidecar
```

The **`model` ↔ `ir` split is the load-bearing decision.** `model.py` is a dumb mirror of XML *syntax*; `ir.py` is *semantics* with no memory of XML. This is the compiler front-end/middle-end separation — adding FBD (same `FlgNet`) or a new output target later touches one phase, not all three. Do not let XML details leak into `ir`, and do not let SCL-rendering concerns leak into `fold`.

Supporting modules sit beside the phases: `instructions.py` (catalog as data), `operand.py` (operand → string, used by both fold and emit), `scl_reconstruct.py` (SCL networks, a different operation from folding).

**Two cross-cutting commitments, present in `ir` from the start:**

- **Traceability.** Every `ir` node carries the source `UId`(s) it came from. Emit produces a `UId → claim` map so any rendered statement is traceable to a net.
- **Loud failure.** Anything the folder cannot interpret becomes an `ir.Unhandled` node carrying its part name + `UId`. Emit renders it visibly (e.g. `// (!) UNHANDLED ...`). **A silent omission in authoritative-looking SCL is the worst possible failure** — never drop a construct quietly.

---

## 5. Locked decisions (do not reverse without asking the maintainer)

- **Python, stdlib-only runtime** (`xml.etree.ElementTree`). If a dependency-free binary is needed later, freeze with **PyInstaller/Nuitka** — do **not** port to C# or add heavy parsing deps.
- **Readability-first SCL, NOT recompilable.** When clarity and round-trip fidelity conflict, choose clarity. The output is for a human/LLM to understand, not to re-import.
- **JSON sidecar from the start.** The `plc-code-analysis` skill consumes the structured map programmatically.
- **CLI: one file at a time.** Batch (multiple files) is a deliberate later add, not v0.
- **Operates on already-exported XML.** Live TIA Openness integration is out of scope.
- **TIA V21 only** for now. Parse namespace-aware but match by **local name** (FlgNet is `/v5`, StructuredText `/v4`, Interface `/v5` — versions drift; local-name matching survives that).

---

## 6. Scope (v0)

**Covered:** FC/FB blocks; LAD/FBD folding (series→AND, `O`→OR, power-rail fan-out, `Negated`→NOT, daisy-chained coils, structural latch detection); SCL network reconstruction; ground-truth interface types; cross-reference table; the instruction set observed in the samples.

**Deferred — parse *losslessly* into the model, but flag rendering rather than emit:**
- GRAPH/SFC networks and STL networks (record a warning, skip rendering).
- Absolute addressing, array access, and `Operation`-template rendering beyond what the samples exercise.

The model's `raw: dict` escape hatch exists so unfamiliar V21 attributes round-trip instead of raising. Use it.

---

## 7. Implementation roadmap

### Phase 1 — Parse (+ `operand.py`)

Implement `parse.parse_document(xml_text) -> model.Document` and `operand.render(access) -> str` together (folded logic needs readable names immediately).

Decoder rules this phase owns (all detailed in the reading guide):
- [ ] Parse namespace-aware; match elements by **local name**.
- [ ] Scope every `UId` lookup table **per compile unit** — `UId`s repeat across networks.
- [ ] **Never deduplicate `Access` nodes** — one node per use.
- [ ] Unescape `&quot;...&quot;` datatypes; set `Member.is_udt` when the datatype was quoted.
- [ ] Read `Informative` values but do not depend on them.
- [ ] Preserve unknown attributes/elements in `raw`.
- [ ] Empty `<NetworkSource />` → `Network.source = None`.
- [ ] `parse_file` already opens with `utf-8-sig` to strip TIA's BOM — keep that.
- [ ] `operand.render`: `#local`; `"DB".field`; `%MW100`; slice `.%X0`; dotted UDT paths; array `name[idx]`; literal constants verbatim. **Slices are real** (FB_SYSTEM has 17) — do not skip them.

**Definition of done:** all six fixtures parse without error into `model.Document`; interface sections + members carry correct names/types; at least one `FlgNet`'s parts/wires verified against the XML by inspection; empty networks resolve to `source = None`.

### Phase 2 — Fold

Implement `fold.fold_network` and `fold.fold_block` per the algorithm already written in `fold.py`'s docstring. Summary:

- [ ] Build a directed pin-graph from wires (first endpoint = source, rest = sinks = fan-out).
- [ ] Classify each part via `instructions.lookup` → `Category`. Unknown name → `ir.Unhandled` (loud).
- [ ] Walk forward from each `Powerrail`. Compose: series (`A.out → B.in`)→`And`; parallel merged at an `O` node→`Or` (n-ary by cardinality); `Negated` pin→`Not`; comparison (`pre/out`)→`Compare`; edge (`PContact`/`PBox`)→`Edge`.
- [ ] Coils → `ir.Assign` (NORMAL/NEGATED/SET/RESET by name). **Daisy-chained coils** (`Coil_A.out → Coil_B.in`) share upstream flow — the second coil's condition is the post-first-coil power flow, not a fresh rung.
- [ ] Flip-flops (`Rs`/`Sr`) → `ir.FlipFlop`. Boxes (`TON`/`Move`/…) → `ir.BoxCall` with the `Instance` rendered. `Call`/`CallInfo` → `ir.UserCall` (params self-documented in the XML).
- [ ] **Latch detection is structural ONLY:** mark an `Assign.is_latch` when a coil's operand reappears as a contact operand feeding back into that coil's own power path. **Never infer a latch from block type.** Motor.xml has no such feedback and must NOT receive a synthesised latch (see §9).
- [ ] SCL networks → hand to `scl_reconstruct.reconstruct` (ordered token/access concatenation, reusing `operand.render`). Do not fold them.
- [ ] STL/GRAPH networks → record a warning, skip rendering.
- [ ] Build the cross-reference table (`tag → [write/read uses]`) and the instruction inventory for the sidecar.

**Definition of done:** `Motor.xml` folds to the reference in §9 (semantic equality — logic, not byte-for-byte); `SingleAlarm_FB.xml` yields an `Rs` flip-flop + `TON` + rising edge + `Eq`/`Lt` comparisons; `FB_SYSTEM.xml`'s SCL network reconstructs to readable SCL and its slice operands render with `.%X0` form.

### Phase 3 — Emit

Implement `emit.emit_scl(decoded) -> str` and `emit.emit_sidecar(decoded) -> dict`.

- [ ] **SCL text:** per network, a `// Network N: <title>` header then the folded statements as SCL. Call out load-bearing constructs (latches, edges). Render `ir.Unhandled` as a visible `// (!) UNHANDLED <part> (UId <uid>)` line.
- [ ] **JSON sidecar** — one dict:

```jsonc
{
  "block": { "name": "...", "kind": "FB" },
  "interface": [ /* sections → members with ground-truth types */ ],
  "networks": [ { "index": 1, "title": "...", "language": "LAD", "warnings": [] } ],
  "xref": { "FI_Forward": [ { "network": 1, "role": "read", "uid": "..." } ] },
  "instruction_inventory": { "Contact": 20, "Coil": 6, "O": 5, "TON": 1 },
  "warnings": [],
  "trace": { "<uid>": "<claim/location>" }
}
```

**Definition of done:** `--format both` writes an SCL file and a JSON file for every fixture; the JSON matches the schema above; an artificially-unknown part shows up as a loud marker AND in `warnings`.

### Phase 4 — CLI wiring + integration

- [ ] Wire `cli.main`: `parse_file` → `fold_block` → `emit_*` → write artifacts to `--output` (or alongside input). **Consult the cli-design craft skill** for argument/error/help UX before finalising.
- [ ] Integrate into the `plc-code-analysis` skill as the LAD/FBD preprocessor (replaces the current "read SimaticML by hand via SKILL.md guidance" path).

---

## 8. Validation corpus (verified by inventory)

The six fixtures, and the constructs each one actually exercises. Confirm by reading the XML — this table is from a structural scan, not a full read of every line.

| Fixture | Block | Networks | Confirmed constructs |
|---------|-------|----------|----------------------|
| `InvertBit` | FC | 4 (1 logic, 3 empty) | negated Coil; minimal net; **empty-network handling** |
| `SimpleDevice` | FC | 4 | Coil, negated Contact, `O` (OR), user **FC call** (`deviceState`), `TemplateValue` |
| `deviceState` | FC | 2 | **`Move`** (×3), **`Add`** (×3), Contact, `TemplateValue` (×6) — arithmetic/box-heavy, no calls |
| `SingleAlarm_FB` | FB | 6 | **`Rs`** flip-flop, **`TON`** (instance), **`PBox`** (rising edge), `Inc`, `Move`, **`Eq`/`Lt`**, `O` |
| `Motor` | FB | 10 (5 logic) | NC Contacts (4 negated), `O` (×5), **daisy-chained Coils**, `TON`, user **FB + FC calls**, **no latch** |
| `FB_SYSTEM` | FB | 16 | **SCL network** + LAD, **`PContact`** (×8), **`RCoil`**, **`RD_LOC_T`** (system FC), `TON`, **bit slices** (×17) |

Use these as regression inputs. Between them they cover the whole v0 instruction set.

---

## 9. Reference — expected fold output for `Motor.xml`

This is the **semantic ground truth** for Phase 2 (exact whitespace/formatting is the emitter's choice; equality is on logic). `Motor` is combinational — note the absence of any seal-in latch on `START_MOTOR`.

```scl
// Network 1 — AUTO MODE
#ON_AUTO := #FI_AutoConditions
            AND NOT #FI_Protection
            AND NOT #FI_Service
            AND (#FI_Forward OR #FI_Reverse);

// Network 2 — OUTPUT / DIRECTION
#FQ_FWD := (#ON_AUTO AND #FI_Forward)  OR (#FI_Service AND #FIQ_ManualForward);
#FQ_REV := (#ON_AUTO AND #FI_Reverse)  OR (#FI_Service AND #FIQ_ManualReverse);
#START_MOTOR    := #FQ_FWD OR #FQ_REV;                 // freshly computed, no feedback
#FQ_SecondSpeed := #START_MOTOR AND #FI_SecondSpeed;   // daisy-chained off the START_MOTOR coil

// Network 3 — WORK TIME COUNTER (FB call)
#WorkTimeCounter(FI_START_COUNTING := #START_MOTOR,
                 FI_Clock_1s       := #FI_Clock_1s,
                 FIQ_TIMER         := #FIQ_WorkTime,
                 FIQ_BUFFER        := #FIQ_CounterBuffer);

// Network 4 — DEVICE STATE (FC call)
deviceState(Alarm      := #FI_Protection,
            Warning    := #FI_Warning,
            Running    := #START_MOTOR,
            Reverse    := #FQ_REV,
            Service    := #FI_Service,
            deviceIcon := #FIQ_ICON);

// Network 5 — CONFIRMATION MONITORING
#Timer(IN := (#FQ_FWD AND NOT #FI_ConfirmationForward)
          OR (#FQ_REV AND NOT #FI_ConfirmationReverse),
       PT := #FI_ConfirmationTime);
#FQ_NoConfirmationWarning := #Timer.Q;
```

(`#Timer` and `#WorkTimeCounter` are the FB's static instance members; render them from the XML's `Instance` element. The remaining `#`-names are interface members.)

---

## 10. Known traps — verify before you trust

- **The catalog is partly inferred.** Only these instructions are **confirmed by samples**: `Contact, Coil, RCoil, O, Move, Add, Inc, Eq, Lt, PBox, PContact, Rs, TON, RD_LOC_T`. The other 15 entries (`SCoil, Le, Ne, Ge, Gt, NContact, NBox, Sr, Sub, Mul, Div, Dec, Calculate, TOF, TP`) are **inferred from the pattern** — when a real block first uses one, verify its pin names/negation against the actual XML before relying on the catalog row.
- **Latch discipline.** Do not synthesise seal-in latches. Drive everything from the wire graph. Motor proves a "motor block" can be fully combinational.
- **`O` is an explicit node, not geometry.** Parallelism is always the `O` part + power-rail fan-out. Never try to reconstruct 2D layout from coordinates.
- **V21 schema vs the guide.** The guide notes a confirmed-vs-unconfirmed feature gap (absolute addressing, GRAPH, STL, `Operation` templates seen in the XSD but not the samples). Parse these into `raw` losslessly; don't invent rendering.
- **Readability ≠ recompilable.** Do not add machinery to make the SCL re-importable; that is explicitly out of scope and will complicate the emitter (daisy chains, ENO threading).

---

## 11. Project conventions

- **Tests are authored separately** by the maintainer. Structure code to be unit-testable per phase (XML→model, model→ir, ir→text), but do not write tests unless asked. CI already runs whatever lands in `tests/`.
- **Do not rewrite `README.md`** — it is initialised and frozen by agreement.
- **Keep the runtime stdlib-only.** `ruff` + `pytest` are dev-only.
- **List changed/added/deleted files** when reporting work.
- **Propose before large design changes.** The architecture and decisions above were settled deliberately; flag any deviation rather than silently refactoring.
- **Decision/status record** lives in the maintainer's Claude Vault (`Czarnak/ClaudeVault`): project file `01_Projects/simaticml-decoder.md`, decision `03_Decisions/DEC-2026-06-02-simaticml-decoder-architecture.md`. If you have access, log significant decisions and status changes there.

---

## 12. References

- `docs/SIMATICML_READING_GUIDE.md` — authoritative format spec (parsing + folding ground truth).
- The scaffold itself — `model.py` and `ir.py` are the type contracts; `fold.py`'s docstring is the folding algorithm; `instructions.py` is the catalog.
- `tests/fixtures/*.xml` — the six-block regression corpus (§8).

---

## Definition of done (v0)

`simaticml-decode <block>.xml --format both` runs on all six fixtures and produces, for each: readable SCL with load-bearing constructs surfaced and any unhandled parts flagged loudly, plus a JSON sidecar (interface, xref, instruction inventory, warnings, trace) matching §7's schema. `Motor.xml` matches §9 semantically. CI is green.
