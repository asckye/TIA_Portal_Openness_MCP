# Current official API audit

The eight local official PublicAPI XML sets are compared with compiled native-call inventories. No Siemens DLL or XML is redistributed.
The current tool grouping is in [the version matrix](version-tools.md) and [the complete name catalog](version-tool-catalog.md).

| Release | Domain signatures | Unique member names | Compiled direct references | Names requiring further review |
|---|---:|---:|---:|---:|
| 14sp1 | 847 | 825 | 98 | 727 |
| 15.1 | 1209 | 1161 | 107 | 1054 |
| 16 | 1476 | 1420 | 109 | 1311 |
| 17 | 1797 | 1719 | 117 | 1602 |
| 18 | 2215 | 2113 | 117 | 1996 |
| 19 | 3714 | 3586 | 128 | 3458 |
| 20 | 4016 | 3860 | 1577 | 2283 |
| 21 | 4480 | 4323 | 1719 | 2604 |

Counts exclude AddIn assemblies, constructors, explicit interfaces, private API and common collection/engineering boilerplate. Signature counts retain overloads; member-name counts combine overloads of one owner/name. Direct references match compiled IL call-site owners and method/property names; comments and inactive source branches are not counted.

The six legacy columns use the selected PLC adapter; V20/V21 use the full compiled engines. A referenced member may be in an unadvertised adapter path, inherited/generic calls can require manual mapping, and reflection can reach some unreferenced members. Therefore these are review candidates, not missing-tool counts, a coverage percentage or proof of native behavior.

The historical PLC migration backlog contained seven pending tool names at baseline
`c5379c1`; this batch adds scoped external-source creation and generation, leaving
five names unimplemented. It is only one migration list. The functional review checks the
engineering workflows those names represent, existing runtime gates, and additional
API families. See the [implementation order](version-tools.md#remaining-tools-and-acceptance).

Reproduce with `scripts/diagnostics/Audit-VersionTools.py --public-api-root <SDK-root>` after building all targets. [Machine-readable summary and SDK hashes](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/version-api-audit.json) identify the exact inputs. Detailed identifier-only candidate lists are written under `bin-build/multi-version/api-audit`.

The previous V21 lexical audit (2.7.42) is preserved in [Git history](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6f3a5e1f2d4554f2adfefce883843c4bb8f5d6d2/docs/reference/openness-coverage.md). It does not describe the newly enabled version routes.

## Functional review

Reviewed on 2026-10-03 against the eight supplied SDK XML sets, current source routes
and the advertised catalogs. The [functional matrix JSON](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/version-feature-matrix.json)
contains exact identifiers, input hashes, source files, tool names and follow-up work
for every row below. It is separate from the generated member-reference audit and
does not redistribute the SDKs. The table covers 29 workflows/families, not all
possible Siemens actions. A broad family can be partial even when it already has
many working tool implementations.

All **N** cells were also checked against assembly metadata with reflection-only
loading of the original V14 SP1 through V18 `Siemens.Engineering.dll`: the relevant
type, property or collection import method is absent. The JSON records these DLL
hashes as well as XML hashes. No TIA session or native engineering operation was
started by that check. **?** cells deliberately make no equivalent absence claim.

- **I — implemented:** an advertised implementation exists for the stated scope;
  this is not native acceptance.
- **P — partial:** some stages/operations exist, but a workflow, format or execution
  scope remains incomplete.
- **B — pending:** the inspected API exists, but the dedicated MCP scope is missing.
- **N — unavailable API:** the exact inspected API is absent from the supplied SDK;
  manual TIA product functionality is a separate question.
- **? — unverified:** an equivalent interface or its behavior has not been established.

| Workflow / inspected scope | 14 SP1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 |
|---|---|---|---|---|---|---|---|---|
| Ordinary PLC block/type/tag-table browsing | I | I | I | I | I | I | I | I |
| PLC block/type/tag-table XML exchange | P | P | P | P | P | P | P | P |
| Create external source from a file | I | I | I | I | I | I | I | I |
| Generate blocks from external source | I | I | I | I | I | I | I | I |
| Compile PLC and read nested diagnostics | P | P | P | P | P | P | I | I |
| Source-to-compile/readback stages | P | P | P | P | P | P | I | I |
| Offline builders targeting the selected XML release | P | P | P | P | P | P | P | I |
| Read ordinary technology-object metadata | I | I | I | I | I | I | I | I |
| Import technology-object XML | N | N | B | B | B | B | I | I |
| Traverse technology-object user groups | N | N | N | N | N | I | I | I |
| Export technology-object XML | B | B | I | I | I | I | I | I |
| Watch-table listing and XML exchange | N | P | P | P | P | P | I | I |
| Hardware catalog search | N | N | N | N | B | I | I | I |
| Create a device with its initial item | B | B | B | B | B | I | I | I |
| Network interface/connection engineering | B | B | B | B | B | B | P | P |
| CAx device exchange | B | B | B | B | B | B | I | I |
| Project/global library engineering | B | B | B | B | B | B | P | P |
| Version Control Interface through MCP | N | N | B | B | B | B | I | I |
| Classic HMI engineering | B | B | B | B | B | B | P | P |
| Unified HMI engineering | ? | ? | ? | ? | ? | B | P | P |
| PLC software-unit engineering | N | N | B | B | B | B | P | P |
| Safety offline-program login for compilation | N | N | N | P | P | P | I | I |
| Modern Startdrive parameter operations | ? | B | B | B | B | B | P | P |
| SiVArc engineering | ? | B | B | B | B | B | P | P |
| Drive Control Chart engineering | ? | ? | B | B | B | B | P | P |
| Continuous Function Chart exchange | ? | ? | ? | ? | B | B | P | P |
| Test Suite case management/execution | ? | ? | ? | B | B | B | P | P |
| Teamcenter integration | ? | ? | ? | ? | B | B | P | P |
| PLC upload/download | ? | B | B | B | B | B | P | P |

All eight-release native acceptance entries remain **NOT RUN**. The compiled
adapters and functional/transport tests are separate evidence. Neither a build nor
a schema-valid example establishes a successful native import or compile.

## Findings that determine implementation order

1. **The source pipeline is available in the official APIs.** Every inspected SDK has
   `PlcExternalSourceComposition.CreateFromFile(string, string)` and parameterless
   `PlcExternalSource.GenerateBlocksFromSource()`. The two foundation execution
   routes are now implemented for root sources. Import accepts ASCII SCL/AWL/DB/UDT;
   other encodings, including Chinese comments, still need version-specific native
   validation. V15.1+ generation uses `GenerateBlockOption.None` and returns native
   generated objects. V14 SP1 only provides a void operation, so its inventory
   changes must not be described as a complete native generated-object list. Existing
   blocks/types may be overwritten. Normal native generation errors are returned
   as `failed` while retaining the session; interruption/readback uncertainty still
   requires a reset. Planning is not import, and generation requires separate
   compilation and readback.
2. **Compilation had an execution gate hidden behind an advertised route.** The
   foundation implementation called an unconditional refusal before its native
   compiler. This batch replaces that blanket refusal with explicit traversal of
   top-level, ungrouped and nested-group devices and all exposed standard/RH states.
   The selected PLC must be Offline; observed unknown/online states and PLCs without
   required providers are refused. HMI/passive devices without exposed state are
   reported through `offlineStateNotExposedByDevices`, without inferring Offline or
   refusing solely for provider absence. The official all-devices-offline prerequisite
   remains, so those states require independent confirmation. Diagnostics use the
   documented typed fields and recursively nested messages, without guessing
   additional dynamic diagnostic attributes.
3. **Technology-object import has a genuine API boundary.** The collection import
   member is absent from the supplied V14 SP1/V15.1 XML and present from V16.
   `TechnologicalInstanceDBGroup.Groups` appears from V19. An older export API does
   not imply a corresponding import API or support for user folders.
4. **Host version and generated XML format are different facts.** This batch makes
   flat UDT and GlobalDB declarations target each exact release's interface format
   and object attributes. Sixteen interface fragments passed the supplied official
   XSDs, but complete-document/native import and CPU/type semantics remain unverified.
   The other six PLC builders still emit V21 candidates, so the family remains
   partial for older hosts. V14 SP1 SCL block XML is also interface-only. A version
   marker change cannot establish compatibility. See the
   [V16 manual](https://cache.industry.siemens.com/dl/files/802/109773802/att_1007204/v1/TIAPortalOpenness_en-US.pdf)
   and [V18 object namespace change](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-for-long-term-stability-in-tia-portal-openness-v18).
5. **Several legacy gaps are exposure gaps.** Watch-table import exists from V15.1,
   catalog search types from V18, and common device creation from V14 SP1. Classic
   HMI, project libraries and CAx APIs also exist in every inspected SDK. Studio's
   V16-V19 VCI implementation is an existing reuse candidate. These families need
   contract-level ports, not duplicate generic implementations or one tool per
   unreferenced member.

For each completed scope, update this table and the JSON together, add its calls and
programming assets to the existing unified example library, and record functional
test evidence separately from native test-project import/compile/readback evidence.
Original V14/V15, uninspected SDKs, missing optional services and untested project
types do not inherit support from a nearby release number.
