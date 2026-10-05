# Unified HMI event actions

Document id: `hmi-unified-actions`

These tools belong to the V20/V21 full engines. Button event code and global script modules are different objects; use the [global-script workflow](global-scripts.md) for modules.

## Choose an entry point

| Tool | Purpose |
|---|---|
| `BuildUnifiedHmiButtonActionScript` | Generate and inspect a recipe offline |
| `EnsureUnifiedHmiButtonAction` | Apply a supported bit action to an existing button |
| `SetUnifiedHmiButtonEventScriptCode` | Set complete button event code |
| `BindUnifiedHmiTagDynamization` | Bind an object property to an existing HMI tag |

Read `GetToolUsage(toolName="...")` for exact arguments, supported events and result fields. Use `GetToolUsage(language="hmi-javascript")` for complete language examples.

The high-level recipe accepts `set-bit`, `reset-bit` and `toggle-bit`. Other recipes may return `applyBlocked=true` or a structural placeholder; that result is not an executable action. Review the actual recipe result rather than assuming every named action is implemented.

## Apply and verify

1. Read the real HMI software, screen, button and tag names from the project.
2. Check the tag's intended source and binding.
3. Build the recipe and inspect its generated script, errors and application status.
4. Apply the supported action using the current tool example.
5. Read back the event code and binding, then inspect the appropriate HMI compile/runtime result before treating the action as complete.

`SetUnifiedHmiButtonEventScriptCode` defaults to `syntaxCheck=false`. In that mode `syntaxCheckStatus=skipped` and no syntax error count is provided: the script was **not checked by TIA**. Offline parsing and a successful property write are also separate from native or runtime verification.

When explicitly using `syntaxCheck=true`, inspect whether the native check ran, was unavailable, or faulted. A native fault can invalidate the TIA session; verify the project state before repeating a write. Do not infer zero errors from a missing field.

See [screen generation](design.md), [tag binding](tag-binding.md) and the [central language catalog](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/tool-examples/languages/catalog.json).
