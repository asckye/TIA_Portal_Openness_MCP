# Foundation protocol 2 regression

Run `python scripts/checks/Test-DotnetSuites.py --suite worker-channel` from the
repository root (minimum 253 passed, zero skipped). The tests exercise the actual
shared channel, including the synthetic `TiaMcpServer.TransportFixture` child
process. No Siemens reference, TIA process, PLC or network service is used.
The separate `Test-FoundationTransport.py` check exercises the real MCP host over
STDIO and a locally created HTTP fixture service.

The names below correspond to assertion texts found with `git grep` in the
preview tests. The preview projects were deleted in step A; the final audit and LegacyHost
assertion disposition are recorded in [adapter-merge.md](../../../../docs/development/adapter-merge.md#第-a-步最终规则核对).
`ProtocolTests` uses controlled byte streams; `ServerTests` observes dispatch
counts and thread ownership; `ProcessTests` exercises real OS pipes.

| Preview test / assertion | Protocol 2 replacement |
|---|---|
| WorkerProtocol: `FreshUnboundHelloRequired`; Endpoint: `no hello terminal`, `nonfresh hello` | `ProtocolTests.FreshUnboundHelloRequired`, `ServerTests.NoHelloTerminal`, `NonfreshHello` |
| WorkerProtocol: `bad hello poisons session`; AsyncPreview: `handshake failed before writes`; HostTransport: `startup token/pid/release/hash/hello-oversized/startup-exit/startup-hang` | `BadHelloPoisonsSession`, `MissingHelloHasNoSentOperation`, `ProcessTests.HandshakeFailedBeforeWrites` (release, both hashes, PID, nonce, version, epoch, bound state, missing/oversized hello, EOF) |
| Endpoint: `duplicate hello terminal`; JsonV2/JsonLegacy: `wrong hello`, `no replay trailing` | `DuplicateHelloTerminal` in all three test classes; `LateProgressAndTrailingFramesPoison`, `TerminalAmbiguityNoReplay("late-hello")` |
| HostPreview: `bad framing poisons before validation` | `BadFramingPoisonsBeforeValidation` on both sides; `RequestByteCapAndFramingBeforeDispatch` (truncation, invalid UTF-8, BOM); process malformed/UTF-8/truncated response cases |
| HostPreview: `request byte cap`, `exact frame cap accepted`; AsyncPreview: `oversized`; HostTransport: `payload rejected before read/allocation` | `OversizedLinePoisonsBeforeParsing`, `ExactFrameCapAccepted`, `RequestByteCapAndFramingBeforeDispatch`; process oversized hello/response |
| Endpoint: `concurrent endpoint poisoned`, `reentrant terminal` | `ConcurrentEndpointPoisoned` in ProtocolTests and ProcessTests; `SwallowedInvalidProgressAndReentryTerminal` |
| WorkerProtocol: `cancel before dispatch has no send or poison`; AsyncPreview: `unsent cancellation recoverable`; HostTransport: `unsent cancellation stays usable` | `CancelBeforeDispatchHasNoSendOrPoison`, `UnsentCancellationStaysUsable` |
| WorkerProtocol: `unknown result poisons host`, `no retry send`; HostTransport: `terminal ambiguity`, `no replay` | `UnknownResultPoisonsHostNoReplay` (timeout, cancellation, write failure, explicit invalidation), `TerminalAmbiguityNoReplay` (actual exit/broken pipe and timeout), `ReadFailureOutcomePreserved("session-fault")` |
| WorkerProtocol: `binding transition observed`, `close advances binding epoch`, `wrong-epoch`, `worker-wrong-epoch` | `CloseAdvancesBindingEpoch`, `UnknownReplyIdOrBindingEpochPoisons`, server before-epoch rejection, process before/after epoch faults |
| Endpoint: `late progress terminal`, `wrong thread terminal`, `swallowed invalid progress terminal`, `progress cap terminal`; JsonV2/JsonLegacy: `progress-id`, `progress-repeat` | `LateProgressTerminal`, `SwallowedInvalidProgressAndReentryTerminal`, `InvalidProgressTerminal`, `LateProgressAndTrailingFramesPoison`, `TrailingFramesPoisonIdlePeer` |
| JsonV2/JsonLegacy: `wrong-id`, `id-type`, `trailing`; WorkerProtocol: `duplicate poisons worker` | `UnknownReplyIdOrBindingEpochPoisons`, malformed string-id case, `TrailingFramesPoisonIdlePeer("second-reply")`, `DuplicatePoisonsWorker` |
| JsonV2/JsonLegacy: `read failure outcome preserved`; WorkerProtocol: `verified read failure keeps session usable`, `verified rejection keeps session usable`, `write-as-read-failure` | `ReadFailureOutcomePreserved` on controlled streams and actual processes; error code/evidence preserved, known failures permit another call, write-as-ReadFailed poisons |
| AsyncPreview: `budget never resets on progress or cleanup`, `progress accepted`; HostTransport: `distinct owned handles`, `other peer usable` | `BudgetNeverResetsOnProgressOrCleanup`, `ProgressAcceptedAndDtoBytesPreserved`, `ProgressAcceptedAndDistinctOwnedHandles` |
| Endpoint: `observer terminal`, `emit failure terminal`; JSON codec payload round trips | `ObserverTerminal`, `EmitFailureTerminal`, `OwningThreadAndRawDtoArePreserved`, `ProgressAcceptedAndDtoBytesPreserved` |

The fixture selects faults through `TIA_FIXTURE_FAULT`; see the InlineData cases
in ProcessTests for the supported names. Cancellation/concurrency are injected
by the host test around the fixture's `timeout` mode. Tests check no second send
after poisoning, and no dispatch for rejected startup/framing. The real worker
smoke additionally verifies the reported file hashes and unbound epoch against
the exact built executable and adapter, then calls ReadState and idle Disconnect.

## Preview details deliberately not carried over

- Four-byte length headers, binary terminators, exchange enumeration/disposal,
  arbitrary `FrameLimits`, exchange-frame limits and protocol-selection adapters
  belong to the discarded framing/IV2Exchange API. Newline framing uses byte caps,
  strict JSON/UTF-8, one continuous reader, a fixed progress cap and one deadline.
  Explicit JSON-RPC/protocol 2 checks replace negotiation and prevent downgrade.
- String `worker_N` ids, separate correlation ids, repeated engine/session
  identity on every frame and RequestLogContext are preview-only envelope/logging
  contracts. Protocol 2 uses a dedicated authenticated launch (nonce, PID, release,
  both file hashes) and strictly increasing numeric request ids; the channel does
  not log payloads. Existing adapter journals are unchanged.
- Project SHA-256, TIA process-start time, full ProjectIdentity/BindingSnapshot,
  operation binding prerequisites and external rebind detection are not wired into
  the Foundation adapter. This task observes its existing managed lifecycle fields
  and checks before/after epochs without adding native probes. Existing Foundation
  admission rules and exact project checks remain in force; this is not native
  binding-identity acceptance.
- Preview sanitized read-failure text, rejection of null results, exception-detail
  suppression and codec normalization would change Foundation's MCP contract.
  Original error messages/evidence, explicit void/null, DTO codecs and raw result
  text are preserved instead. Dual Newtonsoft/STJ differential codec fuzzing stays
  with the preview; Foundation DTO golden tests and response snapshots guard the
  retained payload boundary until P2-04.
- Preview-owned process termination/reaping is not the production ownership policy.
  Channel disposal closes its own pipes and never kills a worker/TIA process.
  Only the synthetic test process is terminated during test cleanup.
- AsyncPreview's `concurrent attempt cannot mutate active request` allowed the
  active request to survive. P4-E2 explicitly requires concurrent channel calls
  to poison, so the Endpoint preview rule is used. The existing LegacyHost
  serialization gate still queues MCP calls before they enter the channel.

These exclusions remain part of the step A deletion record; the suite does
not claim to reproduce all preview assertions or provide real TIA acceptance.

`PreviewRuleTests` adds the deletion audit's missing strict-envelope field/token
cases, launch identity coverage, exact/fragmented request cap, per-request progress
sequence/cap, known-failure epochs, uncooperative writes, and swallowed observer/
emission reentry. Production channel code and native dispatch are unchanged.
