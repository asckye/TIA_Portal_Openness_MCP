# Campaign diagnostics

Use .NET 10 from the repository root. `campaign.cs` replaces camp, rawmsg,
export_full and make_ledger. Connection is lazy: `ledger` and `--help` never
read credentials or contact a server.

```powershell
dotnet run scripts/diagnostics/campaign/campaign.cs -- call GetSessionState '{}'
dotnet run scripts/diagnostics/campaign/campaign.cs -- raw GetSessionState '{}'
dotnet run scripts/diagnostics/campaign/campaign.cs -- export <exportId> <outfile>
dotnet run scripts/diagnostics/campaign/campaign.cs -- run scripts/diagnostics/campaign/plans/plan_base.json 0 --config <config.json>
dotnet run scripts/diagnostics/campaign/campaign.cs -- ledger --output bin-build/historical-ledger.md
dotnet run scripts/diagnostics/campaign/campaign.cs -- ledger --output bin-build/historical-ledger.md --check
```

`call` accepts an optional comma-separated field list (`*` prints all fields).
`raw` prints the V4 MESSAGE and META fields. `export` assembles all pages and
stops if a page fails or its offset does not advance. Arguments accept inline
JSON or `@file.json`. The advertised roster is called directly; other tools
use typed `CallTool(name, arguments)`. Calls do not bypass Workbench approval.

Credentials come from `TIA_MCP_URL` / `TIA_MCP_TOKEN`, or recursively from the
`tia-portal-vm` entry in `~/.claude.json`; explicit `--url` / `--token` override
them. Prefer the environment or the Claude entry to avoid shell history.
The HTTP client bypasses system proxies. Credentials are redacted from
responses, errors, output files and ledger rows.

Copy [config.example.json](config.example.json), set the bundle's path **on
the server**, and choose a run name. `${EXPORT_ROOT}` is resolved recursively
in plan arguments (including embedded JSON/XML strings) to
`<bundle-root>/exports/<run>`. `TIA_MCP_BUNDLE_ROOT` can supply the root;
`--run` overrides the configured run name. A configured `exportRoot` must be
that exact directory. Create its export/scratch directories in the test
environment before using the plan. Exports must never go to the desktop.

The 21 plans are frozen JSON generated once from Python, with LF and no BOM.
Their historical project names, device names, addresses and native actions
still require review for the selected test environment. The plans are live
acceptance inputs; static schema validation does not execute them. A run
appends `ledger/<plan>.jsonl`, reports UNCONFIRMED and stops on an unknown
outcome or a required session reset, and stops if the TIA process or binding
is lost. It does not retry or perform MCP cleanup after an unknown outcome.

`ledger-history.json`, `ledger-runs.jsonl.gz`, `historical_tool_names.json`
and frozen `vm_ledger.json` retain the old evidence. `ledger --check` compares
raw UTF-8 bytes, including BOM and line endings. The current
[real-machine ledger](../../../docs/reference/real-machine-ledger.md) also
contains L5 policy and the Generate-Phase6Plan capability block; this historical
renderer refuses to overwrite it. Generate historical output to a separate
file for review. V3 evidence does not grant V4 native acceptance.
