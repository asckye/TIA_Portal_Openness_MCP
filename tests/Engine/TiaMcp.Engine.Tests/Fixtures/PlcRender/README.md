# PLC render goldens

## Regeneration (P6-62)

From the repository root, run:

```text
python tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender/generate.py
```

The generator invokes `PlcProgramRenderer.Write` through a temporary .NET runner
under `bin-build/P6-62/generator`. It uses only cached NuGet packages and an empty
local package source. It sets the child process `TEMP` and `TMP` to the absolute
`bin-build/P6-62` directory, disables MSBuild node reuse/build servers/shared
compilation and uses inherited directory permissions in the Windows sandbox.
All fifteen block goldens and `Catalog/cookbook.html` are replaced by the full
generated HTML with only source paths normalized. The catalog uses the four
tracked cookbook exports, preserving ambiguous/outside-atlas call labels.
Sixteen identical reviewer pages are written to `bin-build/P6-62/samples`.
No golden is edited by hand, and the design handoff is never copied.

`Catalog/Primer.xml` is a synthetic P6-62 presentation fixture, with no native
export provenance. Its adjacent golden/sample demonstrates NO/NC contacts,
coil lenses, a TON and instance/pins, FBD, SCL, an empty network and the existing
constant-contact inspection finding on one page. `Catalog/cookbook.html` is
checked against all four unchanged cookbook inputs, including call references.

P6-62 changes presentation only: Primer CSS tokens and system dark scheme,
document panels, collapsible interfaces, network cards, contact/coil/TON
geometry and visible inspection notes. Round 2 uses a 640-unit canvas and right
rail, with terminal coils and boxes aligned at 616. Coils and known boolean
box outputs (Q/ENO/QU/QD) connect to the rail. Relocated wire endpoints, shared branch buses and junctions retain
their connections; contact/box sizes and pin pitch remain unchanged. Data-only
outputs are not connected to the rail. Catalog headers and cells use one line
with ellipsis, short labels and a filename whose tooltip holds the source path.
Every light token has an explicit dark counterpart. Export metadata has no
project/PLC identity, so the existing page title is retained. Output is still
one self-contained English HTML file with internal anchors and no scripts.
The earlier round descriptions below record the provenance and historical
geometry checks; their byte-identical statements apply to those earlier rounds.

The ten HTML files in this directory are generated from the ten tracked block XML inputs under
`templates/mcp-full-e2e-verify/plc/blocks`, `templates/mcp-full-e2e-verify/plc/blocks`, and
`templates/plc/block-xml/DB_HMI_Interface.xml`. The golden filename encodes its
repository-relative input path. Only the absolute source path inside the HTML is
replaced with that relative path; SVG, text, whitespace and all other HTML bytes
remain unchanged. `PlcProgramRendererTests.Repository_samples_match_generated_html_goldens`
renders each original XML again and compares the complete HTML.

Provenance: these are repository-maintained cookbook/import test inputs. The four
LAD files in the full-E2E template are copies of the cookbook inputs, as documented
in `templates/mcp-full-e2e-verify/README.md`. Their engineering/version metadata is
preserved. Repository history does not establish which files were exported by a
native TIA session, with a release-specific export log and original artifact hash.
Do not treat an `Engineering version="V21"` field as native export verification.

Real release-export evidence is still missing for **V14 SP1, V15.1, V16, V17, V18,
V19, V20 and V21**. The maintainer must provide original VM exports and provenance
for those releases; the Round 3 reviewer explicitly deferred them without blocking
this round. Synthetic namespace-variant tests cover local-name parsing,
LAD/FBD elements, SCL/STL token decoding, escaping, empty networks and envelopes;
they are not evidence of native format acceptance on those releases.

Round 4 regenerates the complete HTML goldens with routing-only junctions,
contact-style comparisons, inline NOT, edge memory below symbols/trigger boxes,
uppercase built-in instruction/pin names, type-only subtitles and unconnected
output placeholders. Custom FB/FC names and parameter names keep their spelling.
Unknown instructions remain visible and are listed in the page's rendering notes.
The original visual-diff renderer is independently compared byte for byte on all
100 ordered pairs of the ten original inputs (201 files including the inventory).

The additional `Parts/*.xml` inputs are **synthetic Round 4/5 presentation fixtures**,
written for this task, with no external source or native export provenance. Their
V21 marker and FlgNet/v5 namespace select a test syntax, not a release acceptance
claim. Each has a complete adjacent `.xml.html` golden with only the absolute
source path normalized, checked by
`PlcProgramRendererTests.Synthetic_part_fixtures_match_complete_html_goldens`.

| Fixture | Additional coverage |
| --- | --- |
| `Parts/EdgeParts.xml` | P/N contacts, P/N coils, P_TRIG/N_TRIG boxes and edge memory |
| `Parts/BoxParts.xml` | TON/TOF/TP/TONR, CTU/CTD/CTUD, FB/FC parameters, variadic MOVE, typed logic/FBD and unknown fallback |
| `Parts/RoutingParts.xml` | Nested O/Or/wire branches, named EN/IN entry and unknown-entry fallback |

Round 5 corrects the routing fixture: routing-only nodes are contracted before
the shared layout is computed. The three contacts now share one column and the
coil sits to their right on the main row. Tests check every branch endpoint, the
join bus to the right of all three contacts, and the coil's row/column. The TON
`pre` connection retains its real `IN` label. Other known timer/counter/trigger
entries likewise retain IN/CU/CD/CLK; `pre` itself never becomes a visible label.
Only the unknown VendorGate entry stays unlabelled.

`Parts/BranchShapes.xml` and its complete HTML golden add two synthetic geometry
cases: nested groups separated by a real instruction, and a flat three-branch
merge with reversed part order plus a structurally pure in/out connector. They
have the same synthetic provenance as the other Parts fixtures. Tests require
separate inner/outer buses where appropriate, with no bus crossing a symbol.
All twelve previously approved goldens other than RoutingParts stay byte-identical.

The existing cookbook goldens cover O, NOT, all six comparisons, MOVE, ADD, SUB,
MUL, DIV, MOD, normal/negated contacts and coils, S/R coils, TON and conversions.

P6-48 proof artifacts, the unchanged `sample-provenance.json`, and standalone
HTML pages/atlas are retained under `bin-build/P6-48`. The earlier SVG raster
proofs are archived separately in `round2-raster-proofs`. Reviewer-supplied Round 3
browser screenshots are preserved in `round4/reviewer-round3-browser`.
Round 4 reviewer images under `renders/browser/r4` are left intact. Run
`python bin-build/P6-48/round5/browser-screenshots.py` from
the repository root outside the sandbox to measure and screenshot all fifteen
current HTML files using headless Edge into `renders/browser/r5/*.png`.
Commands, exit codes, stderr and HTML/PNG hashes are recorded in
`round5/browser-results.json` and adjacent logs when the script runs.
When Edge fails at its Mojo platform channel with Access denied (0x5), the reviewer
can run this same script outside the sandbox, as required by the Round 5 task.
An older PNG never counts as a fresh screenshot. Browser visual/overflow
verification is not replaced by SVG rasterization.
