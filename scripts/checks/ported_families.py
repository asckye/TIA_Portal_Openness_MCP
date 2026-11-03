"""Read the reviewed availability table also compiled by the host and workers."""
import re
from pathlib import Path

RELEASES = tuple(re.findall(r'(?:Foundation|new TiaVersionDescriptor)\("([0-9.a-z]+)",',
                           (Path(__file__).resolve().parents[2] / 'src/Logic/Siemens/TiaVersionCatalog.cs').read_text('utf-8-sig')))
RELEASE_LIST_PATTERN = r'(?:new\[\]\s*\{[^}]+\}|ReleaseRange\.(?:All\b|Since\("[^"\n]+"\)|Between\("[^"\n]+",\s*"[^"\n]+"\)))'


def expand_releases(expression):
    if expression.startswith('new[]'):
        return re.findall(r'"([^"\n]+)"', expression)
    if expression == 'ReleaseRange.All':
        return list(RELEASES)
    endpoints = re.findall(r'"([^"\n]+)"', expression)
    assert all(key in RELEASES for key in endpoints), ("Unknown release key", endpoints)
    first = RELEASES.index(endpoints[0])
    last = RELEASES.index(endpoints[1]) if len(endpoints) == 2 else len(RELEASES) - 1
    assert first <= last, ("Reversed release range", endpoints)
    return list(RELEASES[first:last + 1])


def parse(source):
    # The optional fifth argument contains action switches; it does not change
    # the tool roster and must not cause a partially supported family to vanish.
    rows = re.findall(r'new Family\("([^"\n]+)",\s*"([^"\n]+)",\s*(' + RELEASE_LIST_PATTERN + r'),\s*new\[\]\s*\{([^}]+)\}\s*(?=[,)])', source)
    assert rows, "No reviewed ported-family availability rows"
    assert len(rows) == len(re.findall(r'\bnew\s+Family\s*\(', source)), "Unparsed ported-family availability row"
    result = {}
    for name, prefix, releases, tools in rows:
        assert name not in result, ("Duplicate ported family", name)
        releases = expand_releases(releases)
        tools = re.findall(r'"([^"\n]+)"', tools)
        assert releases and len(releases) == len(set(releases))
        assert set(releases) <= set(RELEASES)
        assert tools and len(tools) == len(set(tools))
        assert not set(tools) & {t for f in result.values() for t in f["tools"]}, "Duplicate ported tool"
        result[name] = {"prefix": prefix, "releases": releases, "tools": tools}
    overrides = re.search(r'\bToolReleases\s*=\s*new Dictionary<string, string\[\]>\([^)]*\)\s*\{(.*?)\n\s*\};', source, re.S)
    assert overrides or not re.search(r'\bToolReleases\s*=', source), "Unparsed per-tool availability overrides"
    if overrides:
        entries = re.findall(r'\["([^"\n]+)"\]\s*=\s*(' + RELEASE_LIST_PATTERN + ')', overrides.group(1))
        assert len(entries) == overrides.group(1).count('=')
        for tool, releases in entries:
            owners = [f for f in result.values() if tool in f["tools"]]
            assert len(owners) == 1, ("Unknown per-tool availability override", tool)
            owner = owners[0]
            releases = expand_releases(releases)
            assert releases and len(releases) == len(set(releases)) and set(releases) <= set(owner["releases"])
            assert tool not in owner.setdefault("tool_releases", {})
            owner["tool_releases"][tool] = releases
    return result


def families(root):
    return parse((Path(root) / "src/Adapters.Contracts/PortedFamilies.cs").read_text("utf-8-sig"))


def additions(root, release):
    return {tool for family in families(root).values() if release in family["releases"] for tool in family["tools"]
            if release in family.get("tool_releases", {}).get(tool, family["releases"])}


def withheld(root, release):
    return {tool for family in families(root).values() for tool in family['tools']} - additions(root, release)


def foundation_candidates(root):
    source = (Path(root) / "src/Adapters.Contracts/PortedFamilies.cs").read_text("utf-8-sig")
    match = re.search(r'FoundationCandidates\s*=\s*(?:\{([^}]+)\}|Array\.AsReadOnly\(new string\[\]\s*\{([^}]+)\}\s*\))\s*;', source)
    assert match, "Missing reviewed Foundation candidate declarations"
    return set(re.findall(r'"([^"\n]+)"', (match[1] or match[2])))
