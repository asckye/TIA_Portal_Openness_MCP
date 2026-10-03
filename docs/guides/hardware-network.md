# Hardware and network configuration

Document id: `hardware-network`

The tools below belong to the V20/V21 full engines. The V19 foundation engine has a narrower hardware search/device-creation route; it does not expose this entire network tool family. See [version support](../reference/version-tools.md).

## Read, plan, apply and check

1. Connect to the intended project and use `GetProjectTree` / `GetDeviceItemTree` to obtain actual device-item paths.
2. Read `GetDeviceItemNetworkInfo` for interfaces, nodes and current attributes.
3. Read `GetToolUsage(toolName="PlanHardwareNetworkConfiguration")` and prepare a plan using those paths.
4. Apply the relevant operation with its own `GetToolUsage` example.
5. Inspect applied/rejected entries and read back the selected node or CPU settings.
6. Compile the relevant project configuration and save after reviewing diagnostics.

| Tool | Purpose |
|---|---|
| `PlanHardwareNetworkConfiguration` | Offline plan; does not modify TIA |
| `EnsureSubnet` | Create or reuse an Industrial Ethernet / PROFINET subnet |
| `AttachDeviceNodeToSubnet` | Attach a selected interface node |
| `SetCpuCommonSettings` | Write exact exposed CPU device-item attributes |

Use interface indexes returned for the actual device. `settingsJson.exactAttributes` contains exact attribute names; convenient words such as `ip` are not automatically aliases for a writable TIA property. Values such as addresses and subnet names come from the intended network configuration, not copied example values.

These operations edit offline engineering configuration. Their readback confirms the project state, not connectivity to a running CPU. Hardware device creation examples are available via `GetToolUsage(exampleId="sequence/hardware-device")` where listed for the selected release; live reads are covered separately in [online monitoring](online-monitoring.md).
