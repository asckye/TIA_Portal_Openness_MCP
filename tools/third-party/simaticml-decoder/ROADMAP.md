# Roadmap

> Directional roadmap, not a release schedule. Capability claims advance only when the required
> representative evidence is committed and runs without fixture-related skips in CI.

- **Establish a redistributable V21 evidence corpus** — replace the current local-evaluation-only
  corpus with sanitized, licensed, redaction-reviewed SimaticML and SIMATIC SD exports plus
  provenance, expected outputs, and stable capability labels.
- **Qualify native FBD and FBD_IEC** — verify the shared `FlgNet` path against representative
  signal flow, calls, EN/ENO, fan-out, multiple outputs, negation, timers, edges, and mixed-language
  blocks before describing FBD as qualified.
- **Expand advanced `FlgNet` semantics from observed exports** — add traceable support for complex
  access paths, calls, multi-output boxes, conversions, diagnostics, and other instruction families
  only when native source and negative cases are available.
- **Scale project analysis deliberately** — retain deterministic serial project indexing while
  evaluating bounded concurrency, streaming, cancellation, checkpoints, and explicit scale budgets
  as separate milestones.
- **Add a V21-specific SIMATIC SD adapter** — recognize the observed `.s7dcl` dialect, associate
  optional `.s7res` resources by proven identity, preserve unknown fields and source spans, and
  integrate normalized records into the project index.
- **Keep GRAPH as a separate future front end** — target traceable state-machine JSON and
  visualization rather than forcing sequential-control semantics into the current boolean IR.

Read the [detailed advanced translation roadmap](docs/roadmap/advanced-translation.md) and its
[acceptance status](docs/roadmap/advanced-translation-acceptance.md) for phase boundaries and
evidence gates.
