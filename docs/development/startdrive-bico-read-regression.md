# Startdrive exact BICO read regression

The maintainer reported TIA crashing when ManageStartdriveParameter read p2051[0]
(the source of drive send word 1). TIA/Startdrive version, drive model and the last
native call have not yet been supplied. Earlier p1070[0] write acceptance does not
establish p2051[0] read acceptance.

The old read branch selected ReadParameters and built a detailed row. That expanded
Bits and read each bit value, plus limits and enum tables, before reading the requested
value. These extra calls are confirmed in the source; none is yet proven to be the
crash site. Catching a managed exception cannot prevent the TIA process from exiting.

The new branch follows the documented Parameters.Find(name).Value BICO path, looking
in ReadParameters only if the primary view has no matching entry. It reads the selected
value once, formats a BICO reference without reading that source's Value, and returns
name, parameterClass and value under meta.before. It does not enumerate bits or query
limits/enum tables. Explicit dotted bit names still use the existing exact bit resolver.
The existing detailed ReadDriveParameters tool is unchanged. Errors are propagated;
there is no automatic retry on another native view after a failed read.

This changes the single-parameter read response: auxiliary metadata is no longer
included. Existing value paths, including meta.before.value.bicoSource and scalar
meta.before.value.value, are preserved. No parameter denylist or new permission switch
was added. Null is preserved and does not prove that a BICO sink is unconnected.

Seven functional regression checks exercise the production read workflow with callbacks:
primary BICO lookup, one value read, read-only fallback, missing entries, null values,
failure without retry, and exact bit selection. They do not execute Siemens getters.
Full build results are recorded in manifest/release-build.json after validation.
Native p2051[0] acceptance is NOT RUN; the crash cause remains unconfirmed until the
current target and diagnostic evidence are available. Do not automatically replay it.

## Guidance delivered to the calling AI

The initialize instructions and Bootstrap point to GetAuthoringGuide with topic
startdrive-bico. That embedded guide maps the official V20/V21 examples to this
server's actual arguments, identifies version availability, explains BICO/scalar/null
responses, and distinguishes offline project data from online drive values.
GetRecipe with topic startdrive-bico-read provides a sequence with explicit native
acceptance limits. Curated examples appear in tools/list, FindTools and preflight.
Parameter preflight now includes nativeReadsPossible, authoringGuideTopic and the
reported failures, even when dryRun is true. These are guidance, not a new admission
block or an assurance that a native call cannot crash.

Actual STDIO/HTTP checks cover full/lite profiles: initialization, guide retrieval,
FindTools, preflight without a TIA connection, recipe retrieval and CallTool delivery.
The tests never execute the native parameter-read step of the recipe.

Official references:
- [V20 BICO example](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)
- [V21 BICO example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)
