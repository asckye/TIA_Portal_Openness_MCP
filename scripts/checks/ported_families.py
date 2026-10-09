"""Read the reviewed availability table also compiled by the host and workers."""
import re
from pathlib import Path


def parse(source):
    # The optional fifth argument contains action switches; it does not change
    # the tool roster and must not cause a partially supported family to vanish.
    rows = re.findall(r'new Family\("([^"\n]+)",\s*"([^"\n]+)",\s*new\[\]\s*\{([^}]+)\},\s*new\[\]\s*\{([^}]+)\}\s*(?=[,)])', source)
    assert rows, "No reviewed ported-family availability rows"
    assert len(rows) == len(re.findall(r'\bnew\s+Family\s*\(', source)), "Unparsed ported-family availability row"
    result = {}
    for name, prefix, releases, tools in rows:
        assert name not in result, ("Duplicate ported family", name)
        releases = re.findall(r'"([^"\n]+)"', releases)
        tools = re.findall(r'"([^"\n]+)"', tools)
        assert releases and len(releases) == len(set(releases))
        assert set(releases) <= {"14sp1", "15.1", "16", "17", "18", "19", "20", "21"}
        assert tools and len(tools) == len(set(tools))
        assert not set(tools) & {t for f in result.values() for t in f["tools"]}, "Duplicate ported tool"
        result[name] = {"prefix": prefix, "releases": releases, "tools": tools}
    overrides = re.search(r'\bToolReleases\s*=\s*new Dictionary<string, string\[\]>\([^;]+?\{(.*?)\n\s*\};', source, re.S)
    assert overrides or not re.search(r'\bToolReleases\s*=', source), "Unparsed per-tool availability overrides"
    if overrides:
        entries = re.findall(r'\["([^"\n]+)"\]\s*=\s*new\[\]\s*\{([^}]+)\}', overrides.group(1))
        assert len(entries) == overrides.group(1).count('=')
        for tool, releases in entries:
            owners = [f for f in result.values() if tool in f["tools"]]
            assert len(owners) == 1, ("Unknown per-tool availability override", tool)
            owner = owners[0]
            releases = re.findall(r'"([^"\n]+)"', releases)
            assert releases and len(releases) == len(set(releases)) and set(releases) <= set(owner["releases"])
            assert tool not in owner.setdefault("tool_releases", {})
            owner["tool_releases"][tool] = releases
    return result


def families(root):
    return parse((Path(root) / "src/Adapters.Contracts/PortedFamilies.cs").read_text("utf-8-sig"))


def additions(root, release):
    return {tool for family in families(root).values() if release in family["releases"] for tool in family["tools"]
            if release in family.get("tool_releases", {}).get(tool, family["releases"])}
