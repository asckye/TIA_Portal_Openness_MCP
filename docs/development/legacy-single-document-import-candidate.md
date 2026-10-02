# Bounded single-document import candidate

Date: 2026-10-02. **Source candidate only; native acceptance NOT RUN.** No TIA runtime, project, PLC, compile, save, download, repair, renumber, commit or push was performed. Existing full-production `Portal.Blocks.cs` / `McpServer.Documents.cs` are not owned or changed by this candidate.

## Exact static evidence and scope

See [document import research](legacy-document-import-research-20261002.md) for the official V20/V21 manual URLs and attributed exact SDK/XML inspection. The typed call is `PlcBlockComposition.ImportFromDocuments(DirectoryInfo, string, ImportDocumentOptions.None)` exactly once. Both exact SDK XMLs define None as **“Throw if exists”**. This is rejection intent, not proof of atomicity, rollback, normalization or race freedom. Returned `ImportedPlcBlocks` is a `PlcBlockAssociation`; it is enumerated, never queried using an invented association `Find`. The destination composition's `Find` verifies existence.

Only exact releases 20 and 21 are admitted. The first executable subset is **one ordinary global DB** with primitive Bool/integer scalar declarations. It does not implement FC/FB/LAD/FBD/SCL/OB/TYPE, instance DB, arrays, structures, strings, references, expression evaluation, software units, protected/safety documents or general SIMATIC SD import. Broad SD support requires a separately reviewed admission grammar. Major release does not establish installed update level.

## Full lexical admission grammar

After optional UTF-8 BOM, code must be printable ASCII plus TAB/CR/LF. Whitespace and `//` line comments are skipped outside quoted tokens. Block comments, `$`/backslash escapes, multiline strings, single-quoted literals and unknown punctuation are rejected. No regex-derived identity and no skipped unknown statements.

- Optional one leading pragma group `{ ... }`.
- Only unique `S7_Optimized`, `S7_StandardRetain` and `S7_Version` keys. Boolean pragma values must be quoted uppercase `TRUE`/`FALSE`; version is quoted `digits.digits`, 1..3 digits per part. Semicolons separate keys; final semicolon optional.
- Exactly `DATA_BLOCK name VAR declaration+ END_VAR END_DATA_BLOCK EOF`.
- Names are optional-double-quoted ASCII identifiers, 1..128 characters. Structural reserved words are excluded. Declared block name must exactly equal requested basename as wrapper policy.
- Each declaration is `name : type [:= literal] ;`. Variable names are case-fold unique, maximum 1024 declarations.
- Types: `Bool`, `SInt`, `USInt`, `Int`, `UInt`, `DInt`, `UDInt`, `LInt`, `ULInt`.
- Bool literals: `true`, `false`, `TRUE`, `FALSE`. Integer literals: optional sign followed by 1..20 decimal digits.
- Maximum 100000 tokens. Unknown/duplicate pragmas, MLC references, language/manual-number/safety pragmas, extra declarations and trailing tokens all refuse admission.

This is a complete grammar for wrapper eligibility, **not Siemens syntax, semantic, range or content validation**. For example, a 20-digit integer may exceed its native datatype range; native failure remains uncertain and poisons the session. No native validity is inferred from lexical acceptance or mock success. Comments/quoted tokens cannot smuggle an additional admitted declaration.

## Input and target protection

One canonical existing local Windows directory, exact basename, no UNC/device namespace, ADS, ambiguous trailing dot/space, reserved device segment, traversal, redundant separators or reparse ancestry. Directory scan is capped at 4096 entries. Same-stem case aliases, directories and alternate formats are refused. `.s7dcl` is mandatory. Matching `.s7res` is mandatory under the conservative V20 wrapper restriction and optional for V21. Resources are preserved as opaque original bytes; the admitted code grammar has no MLC reference form.

Each present file must contain 1..4194304 bytes. Read handles use `FileShare.Read`, stay held through native invocation, and are closed on every exit. Bytes are never rewritten or staged. SHA-256 covers original code/resource bytes, including BOM, comments, whitespace and newline choices. Apply rechecks the selected file list and hashes while handles remain held.

The target must be an exact ordinary PLC software and existing root/user block group, with no fallback or group creation. Complete ordinary group/block inventory is capped at 1024 groups / 4096 inventory entries; traversal depth uses the existing engine bound. Empty groups participate. Identity registry binds native engineering object identities, including project, PLC, context and target group. Case-fold name collision anywhere in this ordinary inventory refuses import. System/software-unit namespaces are outside the candidate and not claimed inventoried.

## Preview, apply and outcomes

Preview has zero native import/offline/write callbacks. It returns exact project/PID/target/software/group, declaration identity, input hashes, complete collision snapshot, update-level uncertainty and deterministic plan hash. Hash includes policy version, identity, release, paths, exact bytes, inventory, kind/language, native None and validation limitations. Host independently recomputes the hash rather than accepting a matching string with contradictory evidence.

Apply requires `confirm=true`, exact expected project and matching preview hash. Fresh target resolution, positive offline prerequisite, inventory collision comparison and unchanged bytes precede the one typed native call. Preflight is not race protection; documented None supplies native reject-existing behavior. No retry, Override fallback, culture activation, renumber, rollback or deletion occurs.

Every attempted call starts with `Attempted=true`, `MayHaveChanged=true`, and native state unknown. Null result, non-Success (including PartialSuccess), exception, unverified/multiple/wrong identity, wrong parent, failed exact existence readback or post-native handle cleanup failure returns `Status=unknown`, `RequiresSessionReset=true`; engine, worker and host poison the mutation session. No attempted failure claims zero changes. Available native messages and identities are retained; oversized diagnostics or failed enumeration are uncertain, never successful. Transport/malformed-response failures retain existing host poison behavior.

Success requires native Success, exactly one declared GlobalDB with verified target parent and exact destination `Find` identity. `ExistsVerified=true` is **not** `ContentVerified` (always false). Compile/save/download remain `notRun`.

## Verification

Managed aggregate after this candidate: **3552 passed, 0 failed, 4 skipped** (no worker process or Siemens assembly loaded). Focused tests cover lexical/comment/string/encoding refusals, declared identity versus filename, version/resource/collision/inventory gates, byte/target/inventory hash binding, no-call preflight failures, null/partial/failure/exception/identity/readback uncertainty, cleanup failure poisoning, forged host evidence and Windows lexical path aliases. Independent reviewer additionally exercised byte mutation, retained read handles and extra lexical probes.

Exact SDK builds are tracked separately by the parent; not claimed by this test count. Windows read-sharing/filesystem enforcement and actual Siemens behavior/native acceptance remain **NOT RUN**. Ledger is partial, not full response compatibility or manual/native closure. The original import option default (`Override`) is intentionally incompatible: candidate exposes `overwrite=false` only plus explicit review/confirmation.
