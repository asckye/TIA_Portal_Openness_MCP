# Glass Layers WPF design QA

Status: passed

Scope: native WPF view reconstruction of MCP Configurator and Openness Studio, including
English and Chinese pages. This is UI/offline acceptance, not native TIA project acceptance.
Reviewed on 2026-10-03 UTC against the maintainer's local Glass Layers handoff.

## Reference and capture pairing

The supplied `design_handoff_tia_mcp_glass/screens` images are 1200 x 780 at 1x. The matching
implementation captures use WPF RenderTargetBitmap at 1200 x 780, 96 DPI. Each comparison PNG
places the supplied target on the left and the actual WPF capture on the right. The source
design handoff remains local and is not part of the shipped application or repository change.

| Reference | Actual capture | Paired comparison |
| --- | --- | --- |
| configurator-light.png | configurator-light.png | configurator-light-comparison.png |
| configurator-dark.png | configurator-dark.png | configurator-dark-comparison.png |
| studio-blocks-light.png | studio-blocks-light.png | studio-blocks-light-comparison.png |
| studio-blocks-dark.png | studio-blocks-dark.png | studio-blocks-dark-comparison.png |
| studio-vci-dark.png | studio-vci-dark.png | studio-vci-dark-comparison.png |

The local delivery gallery also contains eight Chinese captures: Configurator HTTP and stdio,
Studio program blocks, and Studio VCI, each in Light and Dark. Their filenames contain `-zh-`.
Chinese wording follows the existing operation meanings; no Chinese pixel reference was supplied.
An additional English Configurator stdio/Dark capture checks the local-mode state.

The obsolete Studio MCP-tools target is excluded: the maintainer explicitly required Studio to
remove MCP and call Openness directly. The navigation retains Log instead. No MCP client, server
or tools panel has been restored.

## Visual comparison

| Area | Reference expectation | Implemented and inspected |
| --- | --- | --- |
| Layout | 40px chrome; fixed sidebar/details and flexible center | 1200 x 780 minimum; Configurator 340px detail column; Studio 210px navigation and 320/340px details; matching card rows and gaps |
| Typography | Manrope 400/500/600/700; JetBrains Mono 400/500 | Embedded OFL fonts, native WPF font-resource verification; Windows CJK fallback for Chinese; no downloaded runtime font dependency |
| Color | Light #E7ECF1, Dark #0B1420; #0B7A99/#3DBBDC accent | Theme dictionaries and radial glow brushes; inverted summary/log/diff surfaces; readable Chinese Light/Dark pages |
| Shapes | 16px cards, 10-12px fields/rows, pill actions | Shared control templates, selected rings, status colors, 120ms hover and 160ms sync indicator motion |
| Assets | No bitmap illustration assets | WPF brushes and shapes; embedded fonts with OFL notices; Segoe Fluent window glyphs |
| Copy | Truthful status and real results | Process-running state distinct from TIA project connection; XML overwrite choice preserved; VCI sync distinct from Git push |
| Interaction | Existing commands and state | Same ViewModels, command objects and business code; language/theme changes retain values and selection; source/target sync command switching verified |

No P0-P2 visual findings remain in the reviewed states. These P3 platform differences remain:
WPF and browser text rasterization/line breaks vary slightly; the OS-managed outer window frame
is not included in RenderTargetBitmap; CJK uses Windows font fallback because the bundled Latin
fonts do not contain Chinese glyphs. Card transparency over static glows is the handoff's stated
WPF backdrop-blur fallback. These captures do not claim byte-identical pixels.

## Data and state fidelity

- Screenshots use synthetic data only. No live project, secret, service connection or native
  outcome is claimed. The shipping Configurator retains all 12 profiles; its four-card fixture
  is restricted to tests.
- Program-block counters follow the actual seven fixture rows and four selected rows rather
  than the reference's illustrative 128/6/42 counters. Diff counts follow actual displayed lines.
- Compile/inspection cards project the existing operation log; they show Not run before an
  outcome and return to that state when the log is cleared. Existing native messages are retained.
- Import XML continues to ask for the existing overwrite choice; the button therefore says
  Import XML rather than claiming a fixed overwrite policy. Git scope includes staged changes
  because the existing implementation includes them.
- English/Chinese labels, table states and result summaries switch live. Engineering identifiers,
  paths, source code and previously recorded/native diagnostic text retain their original language.
- Both transports were rendered. Local mode hides address, shared-secret and HTTP-service actions
  while preserving configuration values. Empty/disabled states and existing options remain covered
  by the WPF suite. Offscreen Studio captures retain the Window namescope and drain layout/binding
  work without raising the Window startup handler or connecting to TIA.

## Verification

Configurator: 144 passing offline/configuration/WPF assertions. Studio: 604 passing WPF tests and
36 passing client/bridge tests. V20 and V21 Studio adapters compile with zero warnings and errors.
Protected ConfigCore, ClientProfiles, UpdateCheck and every existing Studio ViewModel retain their
pre-change file hashes. Engine, bridge, native adapter and contract source is unchanged by this work.

The full repository Build-Release workflow and strict bundle validation passed on the final
source: 3035 offline checks per V20/V21 identity and 10 SDK policy checks; generated evidence is recorded in `manifest/release-build.json`,
`manifest/configurator-build.json` and `manifest/package-manifest.json`. Native TIA/project/PLC
acceptance is not performed by these checks. No release is published by this UI task.

Reproduce the captures by setting `TIA_GLASS_SCREENSHOTS` to an output directory and running
`scripts/build/Build-Configurator.ps1 -Test` plus the Studio WPF test project in Release mode.
The shared styles, log presentation and embedded font files live under `tools/ui-glass`.
