# Sources and licence

This package is MIT. The accompanying LICENSE preserves the original repository
copyright and identifies new content by asckye. No Siemens application library,
HMI Template Suite, SAF, VASS, SICAR, ISA/IEC text or third-party SCL is included.
The original project is [TIA_Portal_Openness_MCP](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP).
`sources.json` records the repository snapshot and original blob hashes for
every reused template and licence/notice source.

Reused MIT sources from bulaofen0036-coder/TIA_Portal_Openness_MCP, retained in
this repository:

- `templates/plc/scl-examples/FB_BasicLatch.scl`, `FB_StepSequenceDemo.scl`,
  `FB_TimerCounterDemo.scl`, `FC_BasicScaleLimit.scl`: comments and algorithms
  retained; local body references use # for external TIA SCL.
- `templates/plc/plcbuild-json/udt_basic_status.json`: fields transcribed to SCL.
- `templates/plc/plcbuild-json/db_hmi_interface.json`: fixed-address, standard
  layout transcribed to SCL, including padding and defaults.
- The seven `templates/hmi/unified_*.json` designs: copied with explicit screen
  names. Their palette is reused by the newly authored element groups.

The source repository and its independent-maintenance history are recorded in
the root NOTICE.md. The hardware article is example input, not a redistributed
hardware catalog. All other blocks, UDTs, element groups and rules are new MIT
content. Program_Alarm is an instruction name, not copied Siemens source.
