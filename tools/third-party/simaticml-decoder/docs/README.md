# Documentation

Everything in this tree, grouped by who it is for. A new public document under `docs/` is not
complete until it is listed here.

For the project overview, installation, quick start, and common CLI examples, see the
[root README](../README.md).

## Using the decoder

| Document | What you will find |
| --- | --- |
| [Current capabilities](CAPABILITIES.md) | Evidence-qualified support matrix, output fidelity, corpus limitations, and explicit deferrals |
| [Project-mode input contract](PROJECT_INPUT_CONTRACT.md) | V21 project discovery, identity, origin selection, limits, diagnostics, manifest semantics, and exit behavior |
| [Security policy](../SECURITY.md) | Supported versions, untrusted-input boundary, and vulnerability reporting |

## Understanding the design

| Document | What you will find |
| --- | --- |
| [Architecture](ARCHITECTURE.md) | Decode pipeline, project-index pipeline, filesystem boundary, failure isolation, and verification model |
| [SimaticML reading guide](SIMATICML_READING_GUIDE.md) | Detailed XML, interface, network, access, instruction, wire, and language reference |

## Building and contributing

| Document | What you will find |
| --- | --- |
| [Contributing](../CONTRIBUTING.md) | Development setup, fixture policy, pull-request expectations, and release checks |
| [Root README — Development](../README.md#development) | Ruff, pytest, coverage, Python-version, and CI commands |

## Direction

| Document | What you will find |
| --- | --- |
| [Roadmap](../ROADMAP.md) | Concise directional priorities for the project |
| [Advanced translation roadmap](roadmap/advanced-translation.md) | Detailed evidence, FBD, advanced `FlgNet`, project-scale, SIMATIC SD, and GRAPH phases |
| [Advanced translation acceptance](roadmap/advanced-translation-acceptance.md) | AC-001 through AC-014 requirements and current evidence status |
| [Legacy roadmap location](ADVANCED_TRANSLATION_ROADMAP.md) | Compatibility redirect retained for historical links |
| [Legacy acceptance location](ADVANCED_TRANSLATION_ACCEPTANCE_CRITERIA.md) | Compatibility redirect retained for historical links |

## Project history

[`superpowers/`](superpowers/README.md) contains dated design specs, implementation plans,
decisions, lessons, and reports. It is historical process material, not current behavior
documentation.

The original [implementation plan](IMPLEMENTATION_PLAN.md) and the 2026-07-13
[capability assessment](PROJECT_CAPABILITY_ASSESSMENT.md) are retained as historical snapshots.
Use the architecture and capabilities documents above for current state.
