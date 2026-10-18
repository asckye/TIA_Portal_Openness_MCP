# Frozen engine CLI capture

`engine-cli.json` contains raw stdout/stderr bytes (Base64), exit status, arguments,
capture-root metadata and the SHA-256 of the unmodified V20/V21 production engine.
The capture preceded the CLI changes in P8-04i. Each release has help, schema,
version, read-only doctor, two config profiles without an API, explicit empty audit
verification, a rejected relative installer environment, and two successful config
profiles with local SDK files. No Portal/project was connected or opened; no pip
or group repair was executed. No live or external service was used.

`HostCliTests.Stable_stdout_matches_frozen_engine_bytes` compares six successful or
rejected cases per release byte for byte, including exit statuses and host stderr.
Only the recorded root and explicit data directory relocate to the current test
workspace. The raw fixture never changes during a test run.

The full capture also retains accepted intentional differences: help describes
the host subset, doctor repair hints list all eight releases, an unready host doctor
returns 2 (maintainer decision, 2026-10-18) instead of the captured engine's 1, and config now works
without Siemens API loading. The engine's timestamp/PID/path bootstrap diagnostics
on stderr are absent from the host CLI. P8-04i's result report contains the complete
comparison and decisions; the dedicated host help, doctor, config, syntax and audit
tests assert their new behavior. Existing CliV4 tests and suite thresholds remain.

Failed config writes also change from legacy exit 1 to host exit 2 on all eight
releases (maintainer decision, 2026-10-18). The frozen config samples use --print;
this write failure is covered by HostCliTests.Failed_config_write_returns_two_on_every_release,
not by a captured write attempt. install-plc-tools still passes through pip's
nonzero exit status, including 1.

The fixture is evidence from a Windows machine, including its language and local
environment facts; it is not a native TIA acceptance record. Do not regenerate it
from the host or update it to hide an output difference.
