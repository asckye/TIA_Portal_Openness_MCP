"""Generate the 3.x-to-4.0 tool and input migration tables.

The frozen 3.x contracts, shipped V4 baselines, and the reviewed name mapping
in phase6-review.md are the inputs. --check verifies the checked-in output.
"""

import argparse
import json
import pathlib
import re
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
KEYS = ("14sp1", "15.1", "16", "17", "18", "19", "20", "21")
SOURCE_START = "<summary>A. 全量 current name → 4.0 name（包括不变项）</summary>"
OUTPUT = pathlib.Path("docs/releases/v4.0.0-tool-migration.md")
TICK = chr(96)


def fail(message):
    raise ValueError(message)


def read_json(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


def load_mapping(old_by_key, new_by_key):
    text = (ROOT / "docs/development/phase6-review.md").read_text(encoding="utf-8")
    if SOURCE_START not in text:
        fail("Could not find the generated current-to-V4 mapping table in phase6-review.md.")
    table = text.split(SOURCE_START, 1)[1].split("</details>", 1)[0]
    tick = re.escape(TICK)
    row_pattern = (
        r"^\| " + tick + r"([^" + tick + r"]+)" + tick + r" \| "
        + tick + r"([^" + tick + r"]+)" + tick + r" \| ([^|]+) \|"
    )
    rows = re.findall(row_pattern, table, re.MULTILINE)
    mapping = {}
    declared_keys = {}
    for old_name, new_name, key_text in rows:
        if old_name in mapping:
            fail(f"Duplicate name mapping for {old_name}.")
        mapping[old_name] = new_name
        declared_keys[old_name] = tuple(part.strip() for part in key_text.split(","))

    archived_names = set().union(*(set(old_by_key[key]) for key in KEYS))
    missing = sorted(archived_names - set(mapping))
    if missing:
        fail(f"Name mapping omits archived names: {missing}.")
    for name in sorted(set(mapping) - archived_names):
        actual = tuple(key for key in KEYS if name in new_by_key[key])
        if mapping[name] != name or declared_keys[name] != actual:
            fail(f"{name}: non-archived mapping row is not a V4-only entry.")
    for old_name in sorted(archived_names):
        actual = tuple(key for key in KEYS if old_name in old_by_key[key])
        if declared_keys[old_name] != actual:
            fail(f"{old_name}: mapping availability {declared_keys[old_name]} != 3.x archive {actual}.")
    return {name: mapping[name] for name in archived_names}


def schema_type(schema):
    if "anyOf" in schema or "oneOf" in schema:
        variants = schema.get("anyOf", schema.get("oneOf", []))
        labels = sorted({schema_type(item) for item in variants})
        return "|".join(labels)
    value = schema.get("type")
    if isinstance(value, list):
        return "|".join(sorted(set(value)))
    if value == "array":
        item_type = schema_type(schema.get("items", {}))
        return f"array<{item_type}>"
    if value:
        return value
    if "properties" in schema:
        return "object"
    return "unspecified"


def code(value):
    return f"{TICK}{value}{TICK}"


def cell(value):
    return str(value).replace("|", r"\|").replace("\n", " ")


def required_state(name, required):
    return "required" if name in required else "optional"


def parameter_list(tool):
    schema = tool["inputSchema"]
    props = schema.get("properties", {})
    required = set(schema.get("required", []))
    return ", ".join(
        f"{code(name)}: {code(schema_type(props[name]))} ({required_state(name, required)})"
        for name in sorted(props)
    ) or "none"


def parameter_changes(old_tool, new_tool):
    old_schema = old_tool["inputSchema"]
    new_schema = new_tool["inputSchema"]
    old_props = old_schema.get("properties", {})
    new_props = new_schema.get("properties", {})
    old_required = set(old_schema.get("required", []))
    new_required = set(new_schema.get("required", []))

    pairs = {}
    used_new = set()
    for old_name in sorted(old_props):
        if old_name in new_props:
            candidate = old_name
        elif old_name.endswith("Json") and old_name[:-4] in new_props:
            candidate = old_name[:-4]
        elif old_name == "json" and "spec" in new_props:
            candidate = "spec"
        else:
            candidate = None
        if candidate is not None:
            if candidate in used_new:
                fail(f"Ambiguous parameter mapping to {candidate} in {new_tool['name']}.")
            pairs[old_name] = candidate
            used_new.add(candidate)

    changes = []
    newly_required = []
    for old_name in sorted(old_props):
        old_info = old_props[old_name]
        new_name = pairs.get(old_name)
        old_desc = f"{code(old_name)}: {code(schema_type(old_info))} ({required_state(old_name, old_required)})"
        if new_name is None:
            changes.append((old_desc, "—"))
            continue
        new_info = new_props[new_name]
        new_desc = f"{code(new_name)}: {code(schema_type(new_info))} ({required_state(new_name, new_required)})"
        if (old_name != new_name
                or schema_type(old_info) != schema_type(new_info)
                or (old_name in old_required) != (new_name in new_required)):
            changes.append((old_desc, new_desc))
        if new_name in new_required and old_name not in old_required:
            newly_required.append(new_name)

    for new_name in sorted(set(new_props) - used_new):
        new_info = new_props[new_name]
        new_desc = f"{code(new_name)}: {code(schema_type(new_info))} ({required_state(new_name, new_required)})"
        changes.append(("—", new_desc))
        if new_name in new_required:
            newly_required.append(new_name)

    return changes, sorted(set(newly_required))


def render_release(key, old_doc, new_doc, mapping):
    old_tools = {tool["name"]: tool for tool in old_doc["tools"]}
    new_tools = {tool["name"]: tool for tool in new_doc["tools"]}
    targets = {name: mapping[name] for name in old_tools}
    groups = {}
    for old_name, new_name in targets.items():
        groups.setdefault(new_name, []).append(old_name)

    for old_name, new_name in targets.items():
        if new_name not in new_tools:
            fail(f"{key}: archived tool {old_name} maps to missing V4 tool {new_name}.")
    mapped_targets = set(targets.values())
    additions = sorted(set(new_tools) - mapped_targets)
    for target, sources in groups.items():
        if len(sources) > 1 and target != "GetToolUsage":
            fail(f"{key}: unapproved merge into {target}: {sorted(sources)}.")

    merged_aliases = {
        target: sorted(source for source in sources if source != target)
        for target, sources in groups.items()
        if len(sources) > 1
    }
    merged_names = {alias for aliases in merged_aliases.values() for alias in aliases}
    renamed = sorted(
        (old_name, new_name)
        for old_name, new_name in targets.items()
        if old_name != new_name and old_name not in merged_names
    )
    unchanged = sum(old_name == new_name for old_name, new_name in targets.items())

    out = [
        f"### Release {key}",
        "",
        f"3.x catalog: **{len(old_tools)} tools**; V4 baseline: **{len(new_tools)} tools**. "
        f"Unchanged names: {unchanged}; renamed names: {len(renamed)}; "
        f"merged legacy entries: {sum(len(aliases) for aliases in merged_aliases.values())}; "
        f"V4-only tools: {len(additions)}.",
        "",
        "| Change | 3.x name(s) | V4 name |",
        "|---|---|---|",
    ]
    for old_name in sorted(targets):
        new_name = targets[old_name]
        if old_name in merged_names:
            change = "Merge"
        elif old_name == new_name:
            change = "Unchanged"
        else:
            change = "Rename"
        out.append(f"| {change} | {code(old_name)} | {code(new_name)} |")
    out.append("| Removed without replacement | None | — |")
    for name in additions:
        out.append(f"| Added in 4.0 | — | {code(name)} |")
    out.extend(["", "#### Parameter changes", "",
                "| 3.x input | V4 input |", "|---|---|"])

    parameter_rows = []
    required_additions = []
    for old_name in sorted(old_tools):
        target = targets[old_name]
        # Extra merged entry points share the retained GetToolUsage schema;
        # their old topic-only inputs are summarized below as a merge.
        if old_name in merged_names:
            continue
        changes, newly_required = parameter_changes(old_tools[old_name], new_tools[target])
        for old_value, new_value in changes:
            parameter_rows.append((old_name, old_value, new_value))
        for name in newly_required:
            required_additions.append((target, name))
    for name in additions:
        schema = new_tools[name]["inputSchema"]
        for parameter in schema.get("required", []):
            required_additions.append((name, parameter))

    if parameter_rows:
        for old_name, old_value, new_value in parameter_rows:
            parameter_label = f"{code(old_name)}: " if old_value != "—" else ""
            out.append(f"| {parameter_label}{cell(old_value)} | {cell(new_value)} |")
    else:
        out.append("| None | None |")

    if merged_aliases:
        out.extend(["", "Merged entry input schemas", "",
                    "| Retired 3.x entry | 3.x input | Shared V4 destination inputs |",
                    "|---|---|---|"])
        for target, aliases in sorted(merged_aliases.items()):
            for alias in aliases:
                out.append(
                    f"| {code(alias)} → {code(target)} | {cell(parameter_list(old_tools[alias]))} "
                    f"| {cell(parameter_list(new_tools[target]))} |"
                )

    if additions:
        out.extend(["", "New tools and their required inputs", "",
                    "| V4-only tool | Required inputs in the shipped V4 baseline |",
                    "|---|---|"])
        for name in additions:
            schema = new_tools[name]["inputSchema"]
            required = schema.get("required", [])
            props = schema.get("properties", {})
            values = ", ".join(
                f"{code(parameter)}: {code(schema_type(props[parameter]))}"
                for parameter in required
            ) or "none"
            out.append(f"| {code(name)} | {values} |")

    unique_required = sorted(set(required_additions))
    if unique_required:
        out.extend(["", "New required inputs", "",
                    "| Tool | Required input introduced in V4 |",
                    "|---|---|"])
        for name, parameter in unique_required:
            out.append(f"| {code(name)} | {code(parameter)} |")
    else:
        out.extend(["", "New required inputs: **none** for pre-existing tool mappings.", ""])
    return out


def render():
    old_by_key = {}
    new_by_key = {}
    new_docs = {}
    old_docs = {}
    for key in KEYS:
        old_doc = read_json(f"manifest/history/contracts-v3/baseline/{key}.json")
        new_doc = read_json(f"manifest/contracts/v4/baseline/{key}.json")
        if old_doc.get("release") != key or new_doc.get("release") != key:
            fail(f"Contract release key mismatch for {key}.")
        old_by_key[key] = {tool["name"]: tool for tool in old_doc["tools"]}
        new_by_key[key] = {tool["name"]: tool for tool in new_doc["tools"]}
        old_docs[key] = old_doc
        new_docs[key] = new_doc

    mapping = load_mapping(old_by_key, new_by_key)
    lines = [
        "# 4.0.0 tool and input migration tables",
        "",
        "Generated from the 3.x archive at manifest/history/contracts-v3/baseline, "
        "the shipped V4 baselines at manifest/contracts/v4/baseline, and the reviewed "
        "current-to-V4 map in docs/development/phase6-review.md.",
        "",
        "Each release table lists every archived 3.x name and its V4 destination, "
        "including unchanged names. The generator verifies every archived name's release availability, every "
        "mapping target against that release's V4 baseline, and the complete V4 "
        "inventory after additions. Historical names below are not registered as aliases.",
        "",
    ]
    for key in KEYS:
        lines.extend(render_release(key, old_docs[key], new_docs[key], mapping))
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="fail if the checked-in migration table is stale")
    args = parser.parse_args()
    path = ROOT / OUTPUT
    generated = render()
    if args.check:
        if not path.is_file():
            print(f"Missing generated file: {OUTPUT.as_posix()}", file=sys.stderr)
            return 1
        current = path.read_text(encoding="utf-8")
        if current != generated:
            print(f"Generated file is stale: {OUTPUT.as_posix()}", file=sys.stderr)
            return 1
        print(f"Migration tables match all 8 archived and V4 baselines: {OUTPUT.as_posix()}")
        return 0
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Generated migration tables for all 8 release keys: {OUTPUT.as_posix()}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
