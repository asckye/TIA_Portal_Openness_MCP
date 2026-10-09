# Offline generation planning

`GenerationPlanner.Build(package, machineJson, observed, options, pinnedParents?)`
expands format 1 into `GenerationPlanningResult`: a typed expected project model,
a canonical plan and immutable UTF-8 artifacts. It has no Siemens/MCP dependency
and does not dispatch tools. `StageArtifacts()` writes the artifacts to the
caller-selected absolute `ArtifactRoot`, verifies hashes and refuses replacement
of different files. Planning itself has no file side effects.

The host must supply complete, identity-bound readback. `ProjectModel` adapts
devices, groups, blocks, UDTs, external sources, tables/tags, network bindings,
alarm metadata, screen designs and widgets. Native fingerprint strings are
opaque: the host must establish correspondence with the expected normalized
interface/source/library evidence, using exports or a generation record whose
native fingerprints still match readback. **An artifact SHA-256 is not a Siemens
Code/Interface fingerprint.** Copying an old generation record without verifying
the current object is insufficient. Missing evidence produces a conflict;
partial readback and wrong project identity reject the whole request.

Names use the P8-31a parser, field paths and its existing function whitelist.
`naming.<id>` checks that exact rule; every generated object's name is also
checked against an alternative rule of its kind. Kind names are `project`,
`device`, `network`, `FB`, `FC`, `DB`, `OB`, `UDT`, `instanceDb`, `tagTable`, `tag`,
`externalSource`, `blockGroup`, `typeGroup`, `tagGroup`, `deviceGroup`, `hmiGroup`,
`alarm`, `screen`, `widget`, `screenItem`. Group paths check each segment.
Case conversion, numeric parsing, padding and ordering are invariant. Recursive
naming calls are bounded to eight; expanded fields to 4096 characters. Names
are checked, not silently sanitized.

Contexts expose `machine`, `options`, `station`, `device`, `parent`, `unit`,
`signals` (present roles), `signal` (the current signal or the role map), and
`item`. `unit.tagTable`, `unit.callBlock`, `unit.hmiDb` aliases use the explicit
corresponding naming rule ids. `forEach` visits signals, immediate topology
children of the device parent, devices, stations or flattened topology by
ordinal id. `when` supports existence, equality and membership. Omitted required
signals allocate automatically; optional signals are present only when listed
in device IO. Tags expand before calls regardless of emit order.

IO pools share the physical I/Q bit address space within a station. Explicit
addresses reserve first; automatic signals allocate in station/device/role
order, first free address in pool-id order. Bit/byte/word/double/long-word widths
come from the scalar type. Overlapping pools, physical overlaps, wrong direction,
out-of-range explicit assignments and exhaustion reject expansion. IPv4 pools
use a canonical CIDR network, prefix 1..30, with usable host endpoints; explicit
IPs reserve before automatic stations. Number pools are keyed by declared range
id and kind; `seq()` uses `sequence`, `seq(id)` uses that range, and repeated
evaluation for the same emit/item returns the same number. Alarm numbering adds
the `alarms` range and expands its numbering template. No step relies on a TIA
automatically returned number.

Inherited packages require explicitly supplied, uniquely pinned parents.
`EffectiveGenerationPackage.Resolve` walks the declared chain, replaces entries
by whole id (device rules by deviceType), retains resources under their owning
package/version, rewrites inherited resource/type references and validates the
effective package again. Its content hash binds all parent manifests/resources.
Dependency discovery and version solving remain package-management work. A type
selects the **first declared matching implementation**; a later library variant
does not silently override an earlier SCL fallback.

SCL implementation files must contain named FB/FC/DB/UDT declarations. Files are
split at declaration boundaries so an identical existing object is never
reimported as part of another missing object. The primary logical type must have
one declaration of its kind. Comments/string literals cannot invent declarations;
quoted type/call/global references resolve in the expected model. Generated
instance DBs and call FCs use deterministic LF SCL. Bindings permit literals,
absolute I/Q addresses and quoted symbolic member paths, with no expressions or
injected statements. Interface roles resolve to parameter names; OUT uses `=>`,
IN/IN_OUT use `:=`. Calls depend on their types/instances and bound tags/blocks.

Comparison is by a case-insensitive station/kind/name key (PLC-wide tag names
include table placement in the comparison). Missing objects create; identical
objects skip; differing/unverified objects conflict. Unrelated occupied IO,
slot, IP, block/alarm numbers also conflict. No update/overwrite operation is
generated. External-source readback permits replanning after import succeeded
but generation failed. A completely matching existing snapshot yields no steps.

Dependencies use the same MIT `ImportDependencyPlanner` as
`PlanArtifactImportOrder`. Its tool API keeps the 256-item bound; the separately
compiled generation namespace admits 10000 graph nodes. Dependency ordering
overrides phase priority (call FCs depend on the later `plcTags` phase). Stable
step ids, `dependsOn`, canonical argument digests and artifact hashes are included
in the P8-31a plan hash. Plan id derives from package/machine/readback/options;
neither clock, random ids, OS paths nor current culture are consulted.

Availability reads the embedded explicit release rosters and behavior family
tables; it neither guesses by name nor promotes tool presence to native
acceptance. A needed unavailable tool or unresolved mapping blocks its entire
phase, then dependent phases. Conflicting prerequisites likewise block dependent
phases. Existing identical prerequisites remain usable. New projects explicitly
create; existing V20/V21 plans archive the saved project before changes. The
archive preserves the target binding, unlike SaveProjectCopy. Compile and save
are explicit final actions. Apply must preview/preflight and verify every digest
and identity again; it is not implemented here.

Mappings with insufficient format-1 information are reported in `unavailable`,
with their expected objects retained: IP/PROFINET writes need exact writable
attributes; non-PLC group categories need bindings; library placement needs
P8-31f and an observed exact version; organization blocks need concrete event
interfaces; alarm metadata needs Program_Alarm or HMI tag/class bindings and an
import resource; widgets need a complete screen layout/interface design. A
`hmi.screen` emit with complete `screen`/`items` JSON expands/checks all item
names and maps Ensure + Apply. Mode metadata is descriptive; unit rules emit its
implementation/data types and calls. Static HMI definitions are referenced by
device emits rather than instantiated without a station/unit binding.

The planning host must call `StageArtifacts` before approval. StageImportFiles
returns an execution-created identity, so it cannot
be inserted into a fixed plan and fed into later arguments without replanning.
P8-31d/e must integrate these files with session-owned staging/cleanup and bind
native import previews, backup prerequisites and target paths before Apply.
