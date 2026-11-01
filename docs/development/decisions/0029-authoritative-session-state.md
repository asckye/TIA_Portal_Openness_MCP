# ADR 0029: One managed session view and visible host lifetimes

Status: Accepted for DES-1 implementation · 2026-11-01

The beta.2 machine test found that generation read `binding.identity` while the
PLC worker supplied a flat cached identity. Engine tools could use the project,
but host planning refused it. A Workbench restart also left HTTP hosts running
without showing their version or startup settings.

`SharedSessionState` is the sole host projection of the worker's cached lifecycle
identity. It never reads native objects, queries TIA, or stores another binding.
Foundation host services, runtime query results, Workbench control and the
operator pipe read this view. The supervisor wraps the identity consistently as
`binding.identity`; the operator's existing flat binding fields are a projection
of that identity. Worker generation and the existing worker/pipeline reset
requirement are read from their owners, with no independent reset flag.

The view reports effective cross-reference policy, including release defaults,
the host startup environment and authenticated human session consent. Approval
is read-only information from the existing approval settings. Signing, peer
verification, approval, audit, dispatch lanes and generation write fences retain
their current owners and behavior.

`GetWorkbenchState.data.session` describes the requesting shared MCP session;
`selectedSession` preserves the Workbench's current selection, which can differ.
Reading state never attaches the UI to another session or changes selection.

Host registration adds host version and HTTP prefix. The Workbench lists all
live registrations for its data folder, including hosts without sessions and
older registrations whose version is unknown. PID, start time and executable
are verified against the process before display and immediately before stopping.
For older registrations without an HTTP prefix, the Workbench reads the OS
listener/PID table; it does not probe the endpoint or assume a saved port belongs
to that process.
Human confirmation is required to end a host and its sessions. HTTP restart
validates the saved, protected endpoint configuration before stopping, then
starts the current bundle's host. Stdio hosts must be restarted by their MCP
client, which owns their input/output streams. These actions have no MCP,
operator handshake or Workbench-control operation.

Native call order, arguments and owning threads are unchanged. This decision
does not resolve worker timeout/outcome classification (VMFIX-1), and offline
fixtures do not constitute real TIA acceptance.
