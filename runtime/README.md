# Runtime outputs

`dotnet run --project build-tools/release -- build-multi-version -PublicApiRoot <SDK-root> -Test` produces these ignored local outputs:

| Release | Executable | Profile |
|---|---|---|
| V14 SP1 | `runtime/v14sp1/TiaMcp.FoundationHost.exe` | FoundationHost and PLC worker |
| V15.1 | `runtime/v15.1/TiaMcp.FoundationHost.exe` | FoundationHost and PLC worker |
| V16–V19 | `runtime/v16` through `runtime/v19`, `TiaMcp.FoundationHost.exe` | FoundationHost and PLC worker |
| V20/V21 | `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>` | FoundationHost and PLC worker; lite and full catalogs |
| Workbench | Root `TiaOpenness.exe` launches `runtime/studio/TiaOpenness.exe` | Engineering and MCP configuration in one window; shared MCP engineering session |

Keep each runtime's dependencies and `worker` directory together. V14 SP1 and V15.1
must retain their exact keys; original V14 and V15 are outside the target set.
`dotnet` holds the bundled Microsoft .NET 10 runtime (base, ASP.NET Core and Windows Desktop) that FoundationHost and
the Workbench load first; the PLC workers require .NET Framework 4.8. Keep the complete bundle together, including on an AI-only host.
Matching licensed Siemens PublicAPI assemblies come from the installed TIA product.

The Workbench selects and registers the matching executable. Foundation hosts
support STDIO and authenticated HTTP, `--catalog`, `--public-api` and `--release-key`;
V20/V21 accept `--profile full` for the MCP catalog. All eight releases register `CallTool`; it is an MCP tool, not a CLI verb. The command-line verbs `doctor`, `audit verify`, `schema`, `version`, `config`, `install-plc-tools` and `help` run on `TiaMcp.FoundationHost.exe` for every release; `gen`, `prewarm` and the other engine verbs are retired.
`--offline` explicitly disables native worker calls. Normal startup does not attach:
the worker starts on demand when a native tool is called.

[Version tools](../docs/reference/version-tools.md) and
[build hashes](../manifest/multi-version-build.json) describe the built package.
Native acceptance for the new version routes is still pending.
Published archives retain their recorded scope; use the current delivery package and its manifest for the selected release.

Every release keeps `TiaMcp.PlcWorker.<key>.exe`, its net48 dependencies and the matching adapter in `runtime/v<key>/worker/`. The package no longer contains the V20/V21 engine executables or `tool-catalog.json`; the host builds the tool catalog itself.
