# Verified PLC import fixtures

Each XML file contains the complete staged `arguments.files[].content` from the indicated rc.5 `results-V.jsonl` row, with LF line endings.
The prefix identifies the release. V14 SP1 uses the recorded `seq`; other releases use one-based rows.
The V15.1 rounded-value case starts with the staged DB and reconstructs the subsequent export and patch.

| File(s) | Recorded input and observations | Reconstructed readback |
| --- | --- | --- |
| `14sp1-MCP_RC_FCmin.xml` | Seq 10 stages the minimal network. Seq 65 reports an ObjectList difference. Seq 75–76 show the added empty zh-CN text. | Empty block and network Comment/Title, without SetENOAutomatically. |
| `15.1-N4.xml` | Row 97 stages N4. Rows 113, 118–119 identify the network difference and `SetENOAutomatically=False`. | Same text wrappers, with the recorded spelling of the setting. |
| `16-T3.xml`, `17-MCP_RC_FC5.xml`, `18-T3.xml`, `19-T3.xml`, `20-T3.xml` | Staged in rows 96, 10, 35, 13, 16 respectively. Apply rows 116, 71, 87, 97, 99 report ObjectList. V17 row 72 and V19 row 103 show `SetENOAutomatically=false`. | Same text wrappers. The setting is in the preserved lists for the other three cases and its spelling is inferred there. |
| `14sp1-MCP_RC_DB2_chg.xml` | Seq 10 stages the candidate with Motor/Speed=11. Seq 67, 70–71 document the extra Run sibling and MemoryReserve=100. Original Speed was 9. | Full export expands Run before Speed. None keeps the override. |
| `16-T1.xml`, `17-T1.xml`, `18-T1.xml`, `19-T1.xml` | Staged in rows 96, 10, 35, 13. Apply rows 99, 62, 88, 99 report the extra Run sibling. V16 row 100 and V17 row 63 compare the partial instance with only MemoryReserve=100 added. | Full export expands Grp/Inner, including its nested instance. None keeps Speed=12. V18 and V19 have no direct T1 compare row, so their None shapes are inferred. |
| `17-T2a.xml`, `17-T2b.xml` | Row 10 stages both candidates and the SCL declarations (Speed defaults to 5 and nested Lim to 1). Rows 61, 64–70 show the inherited values, B/In/Lim=4 before reset and an unexpanded None readback. | Full export includes Run, Speed and In/Lim,On. Inherited StartValue elements are absent. None carries MemoryReserve=100. |
| `19-T2a.xml`, `19-T2b.xml` | Row 13 stages both candidates and declarations. Rows 75, 96, 98, 101–102 cover the reset and scalar differences. T2a omits Namespace; T2b already includes it. | Same full expansion as V17. Only T2a needs to restore the empty Namespace. |
| `20-MCP_RC_DB5.xml` | Row 16 stages the candidate and the declaration with D/Lim defaulting to 7. Rows 95 and 100 show the full-style startValue difference and unexpanded D. | Full export expands Run and Lim. The None scalar value of 100 is inferred from the other DB exports. |
| `15.1-MCP_RC_R5DBB.xml` | Row 12 stages the seed. Rows 99–103 use its sparse export and patch Speed from 2.5 to 1.23456789012345 and Level from 7 to 8. Rows 107–109 read Speed as 1.234568. | The patched file itself was not attached. Its members, block texts and two value edits are reconstructed from those rows. Full export adds Run and Count. |

[VerifiedExportStyleTests.Recorded.cs](../../VerifiedExportStyleTests.Recorded.cs) builds the before, full and None documents in memory.
The records supply member paths, values, cultures and preserved names. Object IDs, computed timestamps and lengths, representative full-style member flags,
and unreported element order are fixture values. The text wrapper and its order follow the real [V17 LAD coil export](../../../../FoundationHost/TiaMcp.FoundationHost.Tests/Fixtures/LadRung/17/coil.xml).
The negative cases modify one comparison input at a time. The fallback network and override controls add a full-export-only setting to exercise the None comparison.
