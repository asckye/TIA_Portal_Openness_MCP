---
name: Pre-release report
about: Report a problem found while testing a 4.0 pre-release (v4.0.0-beta.N or -rc.N)
title: "[pre-release] "
labels: pre-release
---

Pre-releases are not verified on a real TIA Portal. Their release notes list what still needs a real-machine check; a report on one of those items is especially useful.

**Package**
- Pre-release tag and package file name (for example `v4.0.0-beta.1`, `TIA_MCP_Delivery_v4.0.0-beta.1_<date>.zip`):
- Package SHA-256, if you checked it:
- Item number in the release notes' "Not verified on a real TIA Portal" list, if the problem is about one:

**Environment**
- Exact TIA release: V14 SP1 / V15.1 / V16 / V17 / V18 / V19 / V20 / V21
- Installed Update/Hotfix and optional products, if known:
- Component: MCP host / Workbench / configurator / updater
- Windows version and MCP client:
- Approval on or off:

**What happened**
Name the tool and the action. Paste the V4 reply with `error.code`, `meta.outcome`, `meta.execution` and `meta.requestId`, and say whether the worker or the TIA process exited.

**To reproduce**
1. Tool and arguments, with secrets and customer identifiers removed
2. Engineering object and the smallest sample that shows it
3. Actual result

**Expected behavior**

**Evidence**
Attach the Workbench audit or diagnostics export for the request ID, if available. Do not attach customer projects, credentials, passwords or unredacted logs.
