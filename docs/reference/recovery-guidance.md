# V4 refusal recovery guidance

FoundationHost adds one `RECOVERY_GUIDANCE` entry on all eight releases to
`meta.warnings` for `INVALID_ARGUMENT`, `PRECONDITION_FAILED`,
`CONFIRMATION_REQUIRED` refusals. Successful
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
write. Workbench confirmation remains an explicit human decision.

An `OUTCOME_UNKNOWN` result applies to that call. Read `GetCallResult(requestId)` and inspect its target before repeating it. A running call returns `in-progress` with `OPERATION_IN_PROGRESS`. An identical write returns `same-call-unsettled` until its completed result has been read. `GetSessionState.data.sessionState.unsettled` lists unknown request IDs. `SaveProject` and `SaveProjectCopy` carry `UNSETTLED_CALLS` during preview and apply while that list is nonempty.
