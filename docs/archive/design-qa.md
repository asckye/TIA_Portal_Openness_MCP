# WPF reference design QA

> 历史记录：v2.7.16 配置器界面对照参考设计的一次性检查结果。所引用的截图和 `bin-build/configurator-tests/` 证据为本机产物，不随仓库分发；当前配置器说明见 [配置指南](../getting-started/configuration.md)。

final result: passed

## Target and scope

User references: `Screenshot 2026-09-17 065247.png` (client), `065252.png` (server), `065256.png` (local).
Implementation: compiled WPF `TiaMcpConfigurator.exe`, using existing configuration and process-management logic.

Compared app-owned content at **1220 × 1062**, with the desktop screenshot margins excluded. Client/local captures select Claude Desktop to match the reference selection. Both reference and implementation are combined in each comparison image. Native Windows window chrome is outside the comparison.

Evidence produced under `bin-build/configurator-tests/`:

- `compare-client.png`, `compare-server.png`, `compare-local.png`: reference on left, WPF on right.
- `server.png`, `client.png`, `local.png`: final standalone renders.
- `client-compact.png`, `client-compact-bottom.png`: 1000 × 720 window, showing both the form and reachable actions/log after scrolling.

## Iteration and corrections

Initial comparison identified two P2 differences: an extra help expander displaced the client status card, and primary actions were visibly narrower than the references. Moved client guidance into the selection summary tooltip/click action; adjusted primary button widths. Recaptured all three pages and compared them again at the same viewport. Both issues are resolved.

Confirmed dark 248-pixel sidebar, numbered navigation, blue active state, page step/title hierarchy, connection diagram, five-column categorized client cards, input groups, separated action row, right-aligned primary actions, and status card placement. No overlapping controls or horizontally clipped actions remain. Compact layout switches client cards to four columns and scrolls the right-hand content.

## Functional checks

53 checks passed, including existing 8-client configuration schema/merge/backup tests, encrypted settings, HTTP readiness/authentication simulation, native argument quoting, plus UI navigation, key generation/show/hide, multi-selection counts, actual local transport labeling, and responsive column layout. These tests do not write actual AI-client configuration or change firewall rules/TIA projects.

## Intentional adaptations and minor differences

- Local connection shows **STDIO / automatic startup**, not the reference's loopback HTTP address, because that is the implemented connection behavior. Its client selector and TIA-path action remain available.
- Client fields use a generic example address rather than copying the screenshot's sample IP into a real connection.
- Native Windows title bar, platform font rasterization, and small tracking/shadow differences remain P3 visual differences. No P0/P1/P2 findings remain.
- UI and isolated configuration verification are complete. Real VM/UAC, actual AI-client connections, and a TIA project session are separate integration checks and are not claimed here.
