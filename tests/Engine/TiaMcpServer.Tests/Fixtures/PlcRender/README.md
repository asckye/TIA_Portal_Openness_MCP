# PLC render goldens

The ten HTML files in this directory are generated from the ten tracked block XML inputs under
`plugin/skill/lad-cookbook`, `templates/mcp-full-e2e-verify/plc/blocks`, and
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
