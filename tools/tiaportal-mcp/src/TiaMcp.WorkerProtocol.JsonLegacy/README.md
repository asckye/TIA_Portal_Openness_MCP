# Strict v2 JSON legacy candidate (unwired)

This separately reviewable codec targets **net461 and net8.0** without changing a
production worker's target framework. It references the existing pure
`TiaMcp.WorkerProtocol` identity and RequestGuard contracts. There are no Siemens,
process, pipe, native, async-adapter or production-worker references.

## Dependency decision

The existing repository pins **Newtonsoft.Json 13.0.4**, also used by PlcWorker,
LegacyHostTests and TiaGitAddIn.Core. This project reuses that exact version.

* Official package: https://www.nuget.org/packages/Newtonsoft.Json/13.0.4
* Versioned MIT license: https://github.com/JamesNK/Newtonsoft.Json/blob/13.0.4/LICENSE.md
* The package's actual `lib/net45/Newtonsoft.Json.dll` asset (not a netstandard
  compatibility guess) is selected for net461. Its .NET Framework dependency
  group is empty. Package metadata declares MIT, and the official license
  requires retaining copyright and permission notices when distributing it.
* The net8.0 build selects the package's actual net6.0 asset.
* `Microsoft.NETFramework.ReferenceAssemblies.net461` 1.0.3 is an optional,
  project-local, private build dependency enabled by `UseReferenceAssemblyPackage`.
* This establishes package/compile compatibility, **not support for Microsoft's
  retired .NET Framework 4.6.1 runtime**, nor successful execution on it.

## Strictness and interchange

Newtonsoft's general parser admits syntax the reviewed v2 protocol forbids.
A bounded recursive grammar front end therefore retains exact numeric lexemes,
object member order and duplicate members, and validates JSON syntax before any
protocol fields are interpreted. Newtonsoft decodes strictly scanned string
lexemes and serializes explicit outgoing envelope objects, with isolated settings
(no process-global DefaultSettings, type-name handling, reflection-based incoming
DTO materialization, date conversion, or floating-point numeric coercion).

The envelope validators and serialized-session flow mirror JsonV2 and reuse the
same core guard. A traversal rejects case-insensitive or escaped duplicate names
at every level, including opaque payloads, and validates Unicode scalar pairing.
Limits remain 1 MiB UTF-8 frames and depth 32. UTF-8 decoding is strict. The writer
preserves numeric lexemes and emits deterministic modern-default escaped strings.
Outgoing frames pass the same admission checks as incoming frames.

`JsonPayload` is an immutable interchange value independent of either library's
DOM. `ToJson()` returns its bounded JSON representation; a modern-only adapter can
map it with `JsonDocument.Parse(payload.ToJson()).RootElement.Clone()` and map back
with `JsonPayload.Parse(element.GetRawText())`. These conversions do not admit a
frame or advance session identity by themselves. Use StrictCodec/JsonSession for
that. The legacy public exchange uses byte[]; no System.Text.Json, System.Memory,
JsonElement, or ReadOnlyMemory dependency is forced onto net461.

Existing JsonV2/AsyncPreview/process adapters still expect their own frame and
JsonElement types. **They have not been wired to this candidate.** A separately
reviewed adapter and Windows/net461 execution remain required. No full production
compatibility or native safety result is claimed.

## Verification (2026-10-02, Linux .NET SDK 8.0.425)

* Both net461 and net8.0 compile: 0 warnings, 0 errors. net461 is compile-only.
* Existing core tests: 105 pass; existing reviewed JsonV2 tests: 343 pass (net8.0).
* Legacy differential/session suite: 5,462 checks pass (net8.0).
* The 343-check source corpus is mirrored with payload/byte-array substitutions;
  `verify_source.py` detects changes to the reviewed source hash.
* Every codec decode in that corpus compares admission and sanitized error code
  with JsonV2; every accepted frame compares complete canonical encoded bytes.
* Extra coverage exhausts all Unicode scalars through U+10FFFF in bounded groups
  as both keys and values; preserves large/exponent numeric lexemes; rejects
  permissive Newtonsoft syntax; checks compound-error precedence and isolation
  from global JSON settings; exercises 4,000 deterministic single-byte mutations.
* These finite tests are evidence, not a proof of equivalence over every possible
  byte sequence or across differing Windows/.NET Unicode globalization tables.

Reproduce from repository root (set DOTNET_CLI_HOME/NUGET_PACKAGES to writable,
project-local directories as appropriate; do not change machine-global settings):

```sh
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
python tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonLegacy.Tests/verify_source.py
dotnet build tools/tiaportal-mcp/src/TiaMcp.WorkerProtocol.JsonLegacy -c Release \
  -m:1 -p:UseSharedCompilation=false -p:UseReferenceAssemblyPackage=true
dotnet build tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonLegacy.Tests -c Release \
  -m:1 -p:UseSharedCompilation=false -p:UseReferenceAssemblyPackage=true
dotnet tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonLegacy.Tests/bin/Release/net8.0/TiaMcp.WorkerProtocol.JsonLegacy.Tests.dll
```

Do not override TargetFrameworks when generating lock files: retain both declared
framework dependency graphs. A transport still needs independent bounded reads,
serialized ownership, cancellation/deadlines and no-replay handling.
