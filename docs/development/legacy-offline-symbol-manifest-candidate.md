# Bounded offline PLC symbol manifest candidate

## Scope and evidence

`BuildPlcSymbolManifestFromXmlPath` now has an isolated .NET 8 candidate implementation. It does not call the existing production `PlcSymbolManifestBuilder.BuildFromXmlPath`, which performs recursive directory discovery, loads XML by path and silently deduplicates names. That production helper is unchanged. Its declaration conventions are reimplemented in a bounded reader because its private extraction cannot be reused without those side effects.

The official [Siemens V21 XML block-interface chapter](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section?contentId=TGNd5apTdFB9WBWcrgOXXg) was reviewed before implementation. It describes Sections/Section/Member, required Name and Datatype, nested members, and separate array subelement forms. Export options can omit unedited/default data. Consequently this tool extracts declarations from a supplied snapshot; it does not establish a complete PLC symbol inventory, resolve external types, or infer import compatibility.

Accepted extraction shapes are deliberately narrow:

- An unqualified `Document` root, with unqualified direct `SW.Tags.PlcTagTable` and/or `SW.Blocks.GlobalDB` objects. `Engineering` and `DocumentInfo` metadata are allowed. Other direct object kinds are rejected.
- Tags at `SW.Tags.PlcTagTable/ObjectList/SW.Tags.PlcTag/AttributeList`, with `Name`, `DataTypeName` and optional `LogicalAddress`. Only these direct scalar tag fields are accepted, and duplicate fields are rejected. Tags must appear directly in the table ObjectList; mixed wrapped/unknown children fail the file.
- GlobalDB `AttributeList/Name` and `AttributeList/Interface/Sections`, where `Sections` and its descendants use exactly `http://www.siemens.com/automation/Openness/SW/Interface/v5`. Exactly one section is accepted, and it must be Static. Its direct children must be Member elements; nested members are followed recursively. Unknown wrappers, wrong-case declaration names, extra sections and hidden declaration-like nodes are rejected. Member metadata is limited to AttributeList, StartValue and Comment; array subelement shapes are rejected rather than silently omitted. External UDT definitions are not expanded.
- Engineering version markers are recorded as observed metadata. Known marker spelling is reported separately from compatibility; unknown or missing markers are explicitly marked. No target V21 argument is required for reading. Unknown interface namespace versions are rejected rather than rewritten or guessed.

`wholeXmlSchemaValidated`, `importValidated`, `programSemanticsValidated`, `nativeCertified`, `referencesResolved`, and `symbolInventoryComplete` are false. Success means bounded extraction produced declarations without input errors or duplicate names, not that a TIA schema or whole program is valid. Compound/non-elementary datatype strings are reported as unresolved. Dot-joined names are display conventions, not certified TIA binding identities.

## Explicit input contract

The old broad `path` argument is deliberately not accepted by this candidate. Supply:

- `inputRoot`: existing absolute caller-owned snapshot directory, not a filesystem root
- `files`: an explicit array of relative XML file names, with slash separators
- `expectedOrigin`: exactly `caller-owned-immutable-export-snapshot`

The origin value is a required caller attestation, not proof of provenance or authentication. The snapshot must contain ordinary XML export files and remain immutable during the call. Do not point it at an untrusted concurrently writable tree or special device files. No directory enumeration occurs; every read must be explicitly listed.

Limits: 32 files, 1 MiB per file, 8 MiB total consumed bytes, 16 relative-path segments, 512 path characters, XML depth 48, 50,000 XML nodes per file, 64 attributes per element, 1,048,576 decoded XML characters per file, 16,384 characters per text node, 1,024 characters per attribute or extracted field, and 10,000 symbols total. Limits are fixed and callers cannot raise them.

Absolute file paths, traversal, dot segments, empty segments, backslashes, alternate streams, glob characters, trailing spaces/dots, case-colliding input paths and symlink/reparse-point ancestry are rejected. Empty/stat-zero-length files are rejected before opening, including ordinary Unix FIFO/socket/device cases that report zero length. This is not proof of regular-file type, and reads are not a hard time-bounded security boundary against hostile special files or concurrent replacement. Root and file ancestry are checked; files use read-only `FileShare.Read`; length and last-write-time are compared before and after reads. These checks detect common mutation but do not provide an atomic no-follow filesystem sandbox, prevent hard links, or defeat hostile concurrent rename on every OS. The immutable, caller-owned snapshot requirement is essential.

XML bytes are bounded before parsing. A first `XmlReader` pass checks depth, node and field limits; a second pass materializes the bounded document. Both prohibit DTDs and set the resolver to null. No external entities, schema fetching, XML network access, file writes, project import, Siemens worker, native SDK or program execution is used.

## Output and failures

Each read source reports only a path relative to the caller's root, byte count and SHA-256 of the exact consumed bytes. Successful sources include Engineering marker and supported interface namespace metadata. Diagnostics use fixed codes and never return exception text or resolved absolute paths. Caller-provided symbol text is intentionally included as data, never executed.

Decoded declaration spelling is preserved without whitespace trimming or quote stripping. Duplicate or case-colliding symbols retain all occurrences and add an error even if their declarations are identical. No first-wins merge occurs. A file parse failure contributes no partial declarations. Valid files may remain in an unsuccessful overall result; callers must check `ok` and `errors` before using them. Unresolved datatype references are explicit and never imply whole-program validation.

## Verification

`OfflineSymbolManifestTests.Run(IMcpServer, Action<bool,string>)` exercises actual SDK dispatch and isolated filesystem fixtures in owned temporary directories. Covered: tags, nested DB members, hashes, version metadata, no certification flags, duplicate/conflicting names, unread-requested files, path traversal and case collisions, DTD/entity/network XML attacks, malformed XML/error redaction, unsupported root/namespaces, ambiguous fields, text/depth/file/aggregate byte limits, cancellation Linux file/directory/root symlinks, mixed valid/hidden declarations, extra sections, duplicate addresses, raw-name preservation, scalar DB-name enforcement, namespace-prefix equivalence, datatype attribute case sensitivity, multibyte UTF-8 byte limits, empty files, and owned Linux FIFO rejection before open. The FIFO task has a three-second completion timeout; the focused suite was also run as a child command with a 30-second outer timeout. Temporary fixture writes belong only to tests.

The focused .NET 8 suite passed 65 assertions on 2026-10-02 with ASP.NET certificate generation disabled. The restored package audit emitted NU1900 because its default local cache path was read-only; compilation and tests succeeded. Native TIA testing and the Windows release build have not been performed, so this remains a cloud-functional candidate, not native certification or a release artifact.
