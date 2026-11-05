# Contributing

Contributions and reports are welcome in English or Chinese. Preserve the original copyrights, [LICENSE](../LICENSE), [NOTICE](../NOTICE.md) and component provenance.

## Branch and version scope

All maintained changes target `master`. There are no maintained version branches. The current release keys are `14sp1`, `15.1`, `16`, `17`, `18`, `19`, `20`, `21`; original V14/V15 are excluded. All eight releases use FoundationHost and a matching PLC worker, with public profile `plc-foundation` and a lite catalog by default. The Workbench attaches to the MCP session through its operator channel; the retained Studio bridge has eight version-specific adapters. See [version scope](../docs/reference/version-tools.md).

Shared behavior belongs in API-independent code; native differences stay in their exact-version adapters. Test every affected release, not only V20/V21. A tool present in one catalog must not be advertised for another without a working implementation and matching contract.

## Reports

Include the exact TIA release, known Update/Hotfix, Windows version, host/worker version, selected runtime, client or Studio, expected behavior, actual result and redacted error text. Use the advertised environment tools and Workbench diagnostics for the selected release.

Do not attach customer projects or credentials. Use the smallest reproducible sample and distinguish a returned error from TIA exiting. [Issue templates](ISSUE_TEMPLATE) collect this information.

## Changes and validation

1. Describe the concrete behavior changed and the affected release profiles.
2. Use the actual schema and `GetToolUsage` library for tool examples, result interpretation and language samples. Update the shared example records when a contract changes; do not add parallel instruction systems.
3. Run functional checks appropriate to the change. Separate offline tests, SDK shape checks, protocol checks, mock UI checks and real TIA acceptance. Record native acceptance as NOT RUN when it was not performed.
4. For actual import/compile acceptance, report the selected test project/PLC, imported identities, compile error/warning counts and readback. A build or successful method return does not establish engineering success.
5. Update affected docs and add user-visible changes to `CHANGELOG.md` using the existing format. Keep dates and versions accurate.

Build outputs are not committed. Siemens SDKs come from a licensed local installation and are not distributed. The host merge gate is `dotnet run --project build-tools/release -- merge-check`; run `merge-check -All` before a release candidate. `prepare -Version <version>` builds, packages and checks the ZIP before uploading a private draft; `publish-prepared` publishes the candidate after maintainer confirmation. There is no nightly. A clean-tree `prepare -Version <version> -BuildOnly` builds and packages on any branch without creating a publishable candidate. Follow [validation](../docs/manuals/validation.md) and the [release workflow](../docs/manuals/release.md); source or test changes require refreshed matching evidence.

Repository checks include:

```powershell
dotnet run --project build-tools/release -- check-repository
dotnet run --project build-tools/release -- check-dead-tool-references
```

Registered suites run through `dotnet run --project build-tools/release -- test-suites -Suite <name>`. In the Codex sandbox, build affected Debug projects and run only affected test classes. Unregistered console test programs use `dotnet run`; `dotnet test` does not execute their assertions. See the validation guide for the correct commands and prerequisites.

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

`check-ratchet -Kind swallowed` runs through the repository check (validate workflow); offline CI runs the
C# static-check self-tests. Its baseline is a
multiset of hashes of the try body, catch clause/filter and catch body, independent of file paths and
line numbers. Comments and code whitespace do not affect hashes; literal content does. Moving a
file therefore needs no new allowance, while copying or changing an unmarked catch fails. Run
`dotnet run --project build-tools/release -- check-ratchet -Kind swallowed -UpdateBaseline` after resolving catches to
remove old entries; routine updates must not use `-AllowGrowth` (reserved for reviewed initialization).

[SwallowedExceptions.Note](../src/Shared/SwallowedExceptions.cs) records only the site, exception type and `HResult`, never `Message` or `ToString()`. It emits the first occurrence per site per process to an installed sink and counts later occurrences. The PLC worker and retained Studio bridge install a stderr sink when `TIA_MCP_LOG_SWALLOWED=1`; otherwise they install no sink. The helper catches its own bookkeeping and sink failures. See the [logging policy](../docs/standards/design.md#67-吞异常的记录).

Do not commit TIA projects, SDKs, generated binaries, logs, local machine paths, scratch data or customer material. Keep real-machine evidence and unresolved limitations linked from the current [acceptance index](../docs/reference/real-machine-ledger.md).

## License

Contributions to this project use the [MIT License](../LICENSE). Existing third-party code retains its own license and notices. This project does not bypass Siemens licensing or ship TIA installation media.
