# Unified HMI theme and layout tools

Document id: `hmi-unified-theme-layout`

These V20/V21 full-engine tools calculate or apply the user's chosen theme and layout. They do not select a product design for the user or establish PLC bindings.

| Tool | Operation |
|---|---|
| `BuildUnifiedHmiThemeDesign` | Build theme execution JSON offline |
| `BuildUnifiedHmiLayoutDesign` | Calculate grid positions and sizes offline |
| `ApplyUnifiedHmiTheme` | Apply theme properties to a real screen |
| `ApplyUnifiedHmiLayout` | Apply calculated layout to a real screen |
| `ApplyUnifiedHmiScreenDesign` | Execute the generated design JSON |

Read `GetToolUsage(toolName="...")` for current JSON shapes, defaults and examples. Build first and inspect the output; offline builders do not change the project. Resolve actual HMI and screen paths, apply the selected changes, then read back affected objects.

The layout builder calculates `left`, `top`, `width` and `height` from row/column settings and grid spacing. Match the screen to the actual target resolution. Use text objects for text labels; a rectangle's geometry is not a text label.

Check object identities, geometry and changed properties before saving. Event code, dynamic bindings and PLC addresses are configured through their own tools, described in [screen generation](design.md), [events](unified-actions.md) and [tag binding](tag-binding.md).
