# Strict JSON v2 candidate (unwired)

This same-source net48/net8.0 project adds JSON to the sibling transport-neutral identity core.
It does not edit or replace modern V20/V21 MCP, legacy worker v1, host launch code,
registrations, native operations or the core's net461 target. It is not a native
acceptance result or a claim of release/version support. The older core README's
"no codec included" describes that original core; this separate project supplies
an experimental codec, not coordinated production integration.

## Wire contract

UTF-8 JSON, at most 1 MiB per frame, maximum depth 32. A transport must enforce
its own bounded read before allocating a frame and bound exchange time and
cancellation. JSON whitespace is accepted; comments, trailing commas, malformed
UTF-8, duplicate keys (including escaped spellings), case-ambiguous properties, unpaired escaped Unicode surrogates,
unknown contract fields and omitted contract fields are rejected. Duplicate and
case checks extend into arguments/results to avoid downstream parser ambiguity.
Arguments/results remain tool-specific payloads; their ordinary arbitrary field
names are not envelope fields and are not rejected as unknown. Arguments must be
an object. Result validation remains mandatory and tool-specific. Paired escaped Unicode
surrogates and ordinary valid Unicode are accepted.

Every frame has exact `version: 2` and a lowercase `type`:

- hello: engine, binding
- request: id, identity, arguments
- reply: id, identity, observedAfter, outcome, result
- progress: id, identity, sequence, percent

Engine includes protocol (also exactly 2), releaseKey, workerSha256,
engineSha256, sessionId. Identity includes requestId, correlationId, engine,
operation, before, expectedAfter. Every binding includes epoch, state, project;
state `unbound` requires explicit null project; state `bound` requires project
with projectSha256, tiaProcessId and tiaProcessStartUtcTicks. Hashes/tokens and
exact releases use the core's canonical validation. Integers are signed 64-bit
JSON integer tokens, not numeric strings, decimals or exponent spellings. PID
also must fit positive Int32. Outcomes are exactly succeeded,
rejectedBeforeOperation, readFailed, unknown. Unknown is representable on wire
but always poisons completion. Missing identity never gets synthesized.

Outer IDs preserve JSON type. The seam explicitly selects Numeric (internal
request ID) or ModernString (`worker_` plus invariant internal request ID).
A string `"1"` is not numeric `1`. This demonstrates an explicit mapping, not
compatibility with an existing JSON-RPC message. Progress echoes the complete
request identity and same typed outer ID; sequence must strictly increase and
percent is an integer 0..100. No progress text/exception payload is accepted.

## Integration seam and limits

`JsonSession` is explicitly v2-only, serialized-owner code. AcceptHello verifies
the exact locally selected release, binary hashes and launch session, and fresh
unbound epoch zero. Call requires registered policy, opaque arguments, a bounded
IV2Exchange and a pure tool-specific result validator. It encodes before marking
dispatch; invalid outbound arguments cancel without a transport attempt. It marks
dispatch before the first possible transport write. Any transport failure,
malformed/mismatched response, progress mismatch, missing/duplicate/trailing reply,
unknown result or result-validation exception faults the session and never retries.
Completion identity, outcome classification and independently observed binding are
checked before the pure result-validator callback runs; the guard commits only
after that validator succeeds. Call returns ValidatedReply with both Outcome and
Result, so verified pre-operation rejection/read failure cannot masquerade as
success merely because the payload looks successful. Enumeration acquisition,
MoveNext and Dispose failures (including after a reply) also poison the session
before invoking the result validator.
The exchange enumerable must end immediately after the final reply; real adapters
must distinguish per-request completion from a persistent channel's end-of-stream.
Do not wire it directly to an unbounded stream or enumerate a permanent pipe.

Error exceptions are fixed codes only and have no inner exception. This seam
emits no logs. Existing RequestLogContext remains the immutable field allowlist;
never log frame bodies, arguments/results, project paths, credentials or transport
exception bodies. Fake IPC checks demonstrate its use after worker validation.
The core worker guard still requires a serialized worker owner and independent
observations of binding before/after execution. JSON cannot establish that those
observations or claimed hashes are true; launcher verification, native observation,
project canonicalization/digest generation and actual worker dispatch remain
unimplemented integration gates.

Use a separately selected launch mode with an explicit v2 handshake only after
both endpoints support it. Never trial-decode old traffic as this format, add
optional identity to v1, or fall back after v2 rejection. Foundation workers now
target net48 for all eight releases, but their Newtonsoft.Json/v1 protocol is
unchanged and this preview v2 codec is not wired there. This project still rejects
net461; the remaining WorkerProtocol net461 targets are pending deletion. Existing host hotspots need coordinated
ownership review and independent native/runtime/legacy compatibility tests.

## Targets, dependencies and verification

The same StrictCodec and JsonSession sources compile for net48 and net8.0. net48
uses official Microsoft System.Text.Json 8.0.6. net8.0 retains the SDK framework
implementation and no new package dependency. Portability changes preserve the
ASCII-only ID predicate, enum/null validation, and strict UTF-8 decoding. The
UTF-8 validation array copy is bounded by the existing 1 MiB precheck.
FrameworkCompatibility.cs supplies only the compiler's record/init marker.

System.Text.Json's actual .NET Framework asset requires net462. Its NuGet
net461 entry is computed through netstandard2.0, not an included net461 asset;
Microsoft documents issues consuming .NET Standard 2.0 from net461. Accordingly,
net461 is not accepted here. In particular 14sp1/15.1/16 workers are not enabled
for v2 by this change. Their existing Newtonsoft.Json/v1 implementation is
unchanged; an equivalent adapter would need independent strictness review.

Official references:
- https://www.nuget.org/packages/System.Text.Json/8.0.6
- https://learn.microsoft.com/en-us/dotnet/standard/net-standard
- https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3

packages.lock.json records exact resolved versions and package hashes. Use locked
restore for the full target set with UseReferenceAssemblyPackage=true. That option
adds official Microsoft reference assemblies only for build-time compilation;
PrivateAssets=all prevents exposing the reference package transitively. Neither
reference assemblies nor the package cache belong in the product runtime.

The new runtime package closure is System.Text.Json 8.0.6,
Microsoft.Bcl.AsyncInterfaces 8.0.0, System.Text.Encodings.Web 8.0.0,
System.Buffers 4.5.1, System.Memory 4.5.5, System.Numerics.Vectors 4.5.0,
System.Runtime.CompilerServices.Unsafe 6.0.0,
System.Threading.Tasks.Extensions 4.5.4 and System.ValueTuple 4.5.0.
All are Microsoft/.NET MIT packages. Original LICENSE.TXT and any supplied
THIRD-PARTY-NOTICES.TXT are preserved under dependency-notices; carry the relevant
notices when redistributing runtime assets. The build-only reference-assembly
package has its separate Microsoft license:
https://github.com/Microsoft/dotnet/blob/master/LICENSE
No package is globally installed. Any future executable integration must preserve
its resolved runtime dependencies and validate binding redirects on Windows.

From the repository root (with a .NET 8 SDK, project-local NUGET_PACKAGES,
DOTNET_CLI_HOME, NUGET_HTTP_CACHE_PATH, NUGET_SCRATCH and XDG_DATA_HOME):

    dotnet restore tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonV2.Tests -p:UseReferenceAssemblyPackage=true --source https://api.nuget.org/v3/index.json --locked-mode
    dotnet build tools/tiaportal-mcp/src/TiaMcp.WorkerProtocol.JsonV2 -c Release -p:UseReferenceAssemblyPackage=true --no-restore -m:1
    dotnet build tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonV2.Tests -c Release -p:UseReferenceAssemblyPackage=true --no-restore -m:1
    dotnet tools/tiaportal-mcp/tests/TiaMcp.WorkerProtocol.JsonV2.Tests/bin/Release/net8.0/TiaMcp.WorkerProtocol.JsonV2.Tests.dll

Tests use in-memory JSON exchanges only. No Siemens assembly, worker launch,
network, filesystem IPC, native writes or VM access is used by the test executable.
A net48 compile is not a Windows runtime test or native/worker acceptance; Windows
net48 execution and package/redirect validation remain required before shipping.
