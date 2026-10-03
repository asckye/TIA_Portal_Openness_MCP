# Current official API audit

The eight local official PublicAPI XML sets are compared with compiled native-call inventories. No Siemens DLL or XML is redistributed.
The current tool grouping is in [the version matrix](version-tools.md) and [the complete name catalog](version-tool-catalog.md).

| Release | Domain signatures | Unique member names | Compiled direct references | Names requiring further review |
|---|---:|---:|---:|---:|
| 14sp1 | 847 | 825 | 94 | 731 |
| 15.1 | 1209 | 1161 | 103 | 1058 |
| 16 | 1476 | 1420 | 105 | 1315 |
| 17 | 1797 | 1719 | 113 | 1606 |
| 18 | 2215 | 2113 | 113 | 2000 |
| 19 | 3714 | 3586 | 124 | 3462 |
| 20 | 4016 | 3860 | 1577 | 2283 |
| 21 | 4480 | 4323 | 1719 | 2604 |

Counts exclude AddIn assemblies, constructors, explicit interfaces, private API and common collection/engineering boilerplate. Signature counts retain overloads; member-name counts combine overloads of one owner/name. Direct references match compiled IL call-site owners and method/property names; comments and inactive source branches are not counted.

The six legacy columns use the selected PLC adapter; V20/V21 use the full compiled engines. A referenced member may be in an unadvertised adapter path, inherited/generic calls can require manual mapping, and reflection can reach some unreferenced members. Therefore these are review candidates, not missing-tool counts, a coverage percentage or proof of native behavior.

The known historical PLC migration backlog remains seven tool names. Additional legacy gaps span HMI, libraries/VCI, hardware/network configuration, software/safety units, online operations and licensed options. See the [backlog and scope](version-tools.md#remaining-tools-and-acceptance).

Reproduce with `scripts/diagnostics/Audit-VersionTools.py --public-api-root <SDK-root>` after building all targets. [Machine-readable summary and SDK hashes](../../manifest/version-api-audit.json) identify the exact inputs. Detailed identifier-only candidate lists are written under `bin-build/multi-version/api-audit`.

The previous V21 lexical audit (2.7.42) is preserved in [Git history](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/6f3a5e1f2d4554f2adfefce883843c4bb8f5d6d2/docs/reference/openness-coverage.md). It does not describe the newly enabled version routes.
