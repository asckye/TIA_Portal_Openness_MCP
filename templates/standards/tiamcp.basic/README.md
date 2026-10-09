# tiamcp.basic 1.0.0

Format 1, MIT, eight PLC releases: 14sp1, 15.1, 16, 17, 18, 19, 20, 21.
Load the whole directory with StandardPackageLoader; every file is inventoried.
See NOTICE.md for the retained MIT sources. No proprietary standards are bundled.

The example has 8 motors, 4 two-position valves, 6 digital sensors, 2 analog
inputs and a unit controller. Planning uses a complete empty software snapshot
bound to `basic-example-project`, with existing PLC1 hardware. It performs no
connection or import. Device tags and IDBs use quoted `U01-device` names. The
reserved `00<unit.id>` controller runs first; physical devices follow; alarms run last.
Each unit must include a unit device whose id is `00` plus its topology unit id
(for example `00U01`); controller ids are unique across units. CycleMs must match the cyclic call interval. Timeouts are bounded scan counters.
Commands are level sensitive: the HMI must clear start/open/reset requests;
stop/close and live faults take precedence. These examples are ordinary control
logic, with no functional-safety claim. Maintenance/manual are mode identifiers;
motion still requires the unit run permit and local command/interlocks.

Device HMI data lives in each IDB's static Hmi UDT and uses symbolic access.
DB_HMI_Interface retains the legacy flat standard-access example and defaults;
it is independent of the new device interfaces. Analog raw inputs are Int words;
FC_BasicScaleLimit requires ascending raw and engineering ranges.
The legacy DB preserves field offsets but its number is assigned by TIA on SCL
import. Assign DB200 explicitly before reusing absolute-address legacy HMI tags.

Program_Alarm is supplied by FB_BasicProgramAlarm on every release for S7-1500.
Set each device parameter programAlarm=false for S7-1200; the control blocks do not use
S7-1500-only instructions. Native S7-1500 alarm text/ID configuration remains a
TIA step. Unified alarm classes are declared for 20/21 only. Unified widget
groups are declarative, with PLC symbols and read/write directions, not faceplates.
Each fault-capable example device has a Program_Alarm adapter bound to its Hmi
error bit; the generated call FC places these adapters after the control calls.

The current P8-31b planner retains alarm metadata and all widget slots but marks
their execution mappings unavailable. Group creation is unavailable on 14sp1–19;
SCL imports stay at root so PLC code remains plannable on all eight releases.
Desired groups are 00_System, 10_Modes, 20_Units/U01 and 90_Hmi. Placement and
complete HMI item/tag/alarm mappings need P8-31d/e. Invoke FC_U01_Calls from the
target's cyclic OB; the framework does not synthesize an OB with an assumed event.

Golden plans and the generated FC are stored under the planning test project's
Golden/Basic directory, outside this package: inventorying a plan containing its
own package hash would create a hash cycle. The test suite compares canonical
UTF-8 bytes across repeated runs and four cultures on Windows/Ubuntu CI. Static
analysis is heuristic; VM import/compile on V17, V19 and V21 is still required.
