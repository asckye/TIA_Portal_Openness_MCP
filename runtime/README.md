# Runtime outputs

`Build-MultiVersion.ps1 -PublicApiRoot <SDK-root> -Test` produces these ignored local outputs:

| Release | Executable | Profile |
|---|---|---|
| V14 SP1 | `runtime/v14sp1/TiaMcpServer.exe` | PLC foundation |
| V15.1 | `runtime/v15.1/TiaMcpServer.exe` | PLC foundation |
| V16–V19 | `runtime/v16` through `runtime/v19`, `TiaMcpServer.exe` | PLC foundation |
| V20/V21 | `runtime/v20` and `runtime/v21`, `TiaMcpServer.exe` | Full engine |
| Studio | `runtime/studio/TiaOpenness.exe` | Direct Openness, eight adapters |

Keep each runtime's dependencies and `worker` directory together. V14 SP1 and V15.1
must retain their exact keys; original V14 and V15 are outside the target set.
Foundation hosts require .NET 8 / ASP.NET Core 8 and the worker's .NET Framework;
full engines require .NET Framework 4.8; Studio also needs .NET 10 Desktop Runtime.
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
