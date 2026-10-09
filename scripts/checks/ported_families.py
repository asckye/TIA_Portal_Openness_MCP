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
    return result


def families(root):
    return parse((Path(root) / "src/Adapters.Contracts/PortedFamilies.cs").read_text("utf-8-sig"))


def additions(root, release):
    return {tool for family in families(root).values() if release in family["releases"] for tool in family["tools"]}
