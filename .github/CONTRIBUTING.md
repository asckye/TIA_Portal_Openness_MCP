# Contributing

Contributions and reports are welcome in English or Chinese. Preserve the original copyrights, [LICENSE](../LICENSE), [NOTICE](../NOTICE.md) and component provenance.

## Branch and version scope

All maintained changes target `master`. There are no maintained version branches. The current release keys are `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`; original V14/V15 are excluded. V14 SP1–V19 use the PLC foundation host and matching worker, while V20/V21 use the full engine. Studio uses eight direct Openness adapters. See [version scope](../docs/reference/version-tools.md).

Shared behavior belongs in API-independent code; native differences stay in their exact-version adapters. Test every affected release, not only V20/V21. A tool present in one catalog must not be advertised for another without a working implementation and matching contract.

## Reports

Include the exact TIA release, known Update/Hotfix, Windows version, application/engine version, selected runtime, client or Studio, expected behavior, actual result and redacted error text. The full engines provide the `doctor` CLI; foundation hosts use their advertised environment tools. Do not apply full-engine CLI switches to foundation executables.

Do not attach customer projects or credentials. Use the smallest reproducible sample and distinguish a returned error from TIA exiting. [Issue templates](ISSUE_TEMPLATE) collect this information.

## Changes and validation

1. Describe the concrete behavior changed and the affected release profiles.
2. Use the actual schema and `GetToolUsage` library for tool examples, result interpretation and language samples. Update the shared example records when a contract changes; do not add parallel instruction systems.
3. Run functional checks appropriate to the change. Separate offline tests, SDK shape checks, protocol checks, mock UI checks and real TIA acceptance. Record native acceptance as NOT RUN when it was not performed.
4. For actual import/compile acceptance, report the selected test project/PLC, imported identities, compile error/warning counts and readback. A build or successful method return does not establish engineering success.
5. Update affected docs and add user-visible changes to `CHANGELOG.md` using the existing format. Keep dates and versions accurate.

Build outputs are not committed. Siemens SDKs come from a licensed local installation and are not distributed. `Build-Release.ps1` builds the full engines/configurator; `Build-MultiVersion.ps1` builds and validates the other runtimes and Studio adapters. Formal packaging runs both required stages and verifies their manifests. Follow [validation](../docs/development/validation.md) and the [release workflow](../docs/development/release-workflow.md); source or test changes require refreshed matching evidence.

Repository checks include:

```powershell
python scripts/checks/Check-Repository.py
python scripts/checks/Check-DeadToolReferences.py
```

The full-engine offline suite is a console test program, invoked with `dotnet run`, not `dotnet test`; other projects use their documented test runner. See the validation guide for the correct commands and prerequisites.

## Repository conventions

Use an imperative English commit message without AI attribution trailers. Preserve source style and `.gitattributes`; do not apply a blanket BOM rule to every language or export format. Programming examples document their specific encoding and target-version requirements, including the foundation external-source ASCII boundary.

Retained empty or exception-discarding catches require `/* swallow(<category>): <reason> */`
on the catch line or as the first item in its block. Give a concrete reason; the closed category list is
`cleanup`, `teardown`, `logging-failure`, `probe-optional`, `enumerate-optional`, `native-fallback`,
`parse-fallback`, `env-probe`, `ui`, `fail-open-guard`, `privacy`. For example:

```csharp
catch (IOException) /* swallow(cleanup): temporary file cleanup must not replace the operation result */
{
}
```

`Check-SwallowedExceptions.py` runs through the repository check (validate workflow); offline CI runs its
self-tests. Its baseline is a
multiset of hashes of the try body, catch clause/filter and catch body, independent of file paths and
line numbers. Comments and code whitespace do not affect hashes; literal content does. Moving a
file therefore needs no new allowance, while copying or changing an unmarked catch fails. Run
`python scripts/checks/Check-SwallowedExceptions.py --update-baseline` after resolving catches to
remove old entries; routine updates must not use `--allow-growth` (reserved for reviewed initialization).

The [P2-03 logging policy](../docs/development/response-and-errors.md#日志与检查) specifies the planned
`SwallowedExceptions.Note(string site, Exception ex)` helper: record only the site, exception type's
full name and `HResult`, never `Message` or `ToString()`. Log the first occurrence per site per process
and only count later occurrences; the helper must never throw or recursively log its own failures.
Output is off by default. `TIA_MCP_LOG_SWALLOWED=1` makes the engine, the Studio bridge and the Foundation
worker write one stderr line per site; without it the engine only logs at `ILogger` Debug (category
`TiaMcpServer.Swallowed`), which shows when Debug logging is already enabled.
Do not write swallowed-exception diagnostics to `calls-*.jsonl`, `InvocationJournal` counters or
response metadata. Keep privacy-related stderr discards and logging-failure catches; do not change
native catches to rethrow before L5 acceptance. The helper and runtime wiring belong to P2-03b;
this check does not introduce runtime logging.

Do not commit TIA projects, SDKs, generated binaries, logs, local machine paths, scratch data or customer material. Keep real-machine evidence and unresolved limitations linked from the current [acceptance index](../docs/reference/real-machine-ledger.md).

## License

Contributions to this project use the [MIT License](../LICENSE). Existing third-party code retains its own license and notices. This project does not bypass Siemens licensing or ship TIA installation media.
