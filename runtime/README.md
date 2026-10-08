# Runtime outputs

`dotnet run --project build-tools/release -- build-multi-version -PublicApiRoot <SDK-root> -Test` produces these ignored local outputs:

| Release | Executable | Profile |
|---|---|---|
| V14 SP1 | `runtime/v14sp1/TiaMcp.FoundationHost.exe` | PLC foundation |
| V15.1 | `runtime/v15.1/TiaMcp.FoundationHost.exe` | PLC foundation |
| V16–V19 | `runtime/v16` through `runtime/v19`, `TiaMcp.FoundationHost.exe` | PLC foundation |
| V20/V21 | `runtime/v<key>/TiaMcp.FoundationHost.exe --release-key <key>` | Full catalog, engine worker in `worker/` |
| Unified desktop | Root `TiaOpenness.exe` launches `runtime/studio/TiaOpenness.exe` | Engineering and MCP configuration in one window; eight Openness adapters |

Keep each runtime's dependencies and `worker` directory together. V14 SP1 and V15.1
must retain their exact keys; original V14 and V15 are outside the target set.
`dotnet` holds the bundled Microsoft .NET 10 runtime (base, ASP.NET Core and Windows Desktop) that the foundation hosts and
the unified desktop load first; workers and full engines require .NET Framework 4.8. Keep the complete bundle together, including on an AI-only host.
Matching licensed Siemens PublicAPI assemblies come from the installed TIA product.

The configurator selects and registers the matching executable. Foundation hosts
support STDIO and authenticated HTTP, `--catalog`, `--public-api` and `--release-key`;
they do not implement the full engine's `doctor`, `gen`, `--profile` or `CallTool` CLI.
`--offline` explicitly disables native worker calls. Normal startup does not attach:
the worker starts on demand when a native tool is called.

[Version tools](../docs/reference/version-tools.md) and
[build hashes](../manifest/multi-version-build.json) describe the built package.
Native acceptance for the new version routes is still pending.
v3.2.0 and later complete release archives include all eight runtimes and Studio; older archives retain their original scope.

V20/V21 keep net48 dependencies, the matching adapter and `tool-catalog.json` in `runtime/v<key>/worker/`. The catalog is generated from the woven worker and its hash is checked by the host. CLI verbs (`doctor`, `gen`, `prewarm`) remain available through `worker/TiaMcp.Engine.V<key>.exe`.
