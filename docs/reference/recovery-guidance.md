# V4 refusal recovery guidance

Both the Foundation host and the V20/V21 engine add one `RECOVERY_GUIDANCE`
entry to `meta.warnings` for `INVALID_ARGUMENT`, `PRECONDITION_FAILED`,
`SESSION_RESET_REQUIRED`, and `CONFIRMATION_REQUIRED` refusals. Successful
responses do not receive this warning. The original error code, message,
parameter, outcome, and execution evidence remain intact.

The warning message gives the next step. Its `details.getToolUsage` is a
`GetToolUsage` argument object with `toolName` and, for an example with an
action/operation selector, `operation`. `details.releaseKey` identifies the
release. `details.exampleArguments` copies that release's default reviewed
example arguments from the same embedded library used by `GetToolUsage`.
It contains sample targets and placeholders, never caller arguments.
`details.validation` retains the library's validation statement; a schema
check does not establish native execution acceptance.

Example arguments are limited to 4096 UTF-8 bytes. If the complete example
exceeds that bound or is unavailable, `exampleArguments` is null and
`exampleOmitted` is true; retrieve the complete example with `getToolUsage`.
The warning's `bindingMeaning` requires resolving and reviewing placeholders
against the current session. Guidance does not approve, retry, or execute a
write. Session reset and Workbench confirmation remain explicit caller actions.
