"""Package committed source and complete V20/V21 runtimes, without private build files."""
import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
git = sys.argv[1] if len(sys.argv) > 1 else "git"
source_commit = subprocess.check_output([git, "rev-parse", "HEAD"], cwd=root).decode().strip()
assert not subprocess.check_output([git, "status", "--porcelain", "--untracked-files=normal"], cwd=root).strip(), "Commit the reviewed source before packaging a release"
package = "TIA_MCP_Delivery_v2.7.2-asckye.6-defects.1_V20-V21"
out = root / "bin-build/defects-2.7.2.6"
out.mkdir(parents=True, exist_ok=True)
names = subprocess.check_output([git, "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode("utf-8").split("\0")
files = {}
for name in sorted(set(names)):
    if not name:
        continue
    path = (root / name).resolve()
    assert path.is_relative_to(root)
    if not path.is_file():  # deleted tracked source file
        continue
    assert not name.startswith((".git/", "bin-build/"))
    assert "PublicAPI/" not in name and "source-review/" not in name
    assert not name.endswith((".log", ".pdb", ".patch"))
    files[name] = path.read_bytes()

assert sorted(n for n in files if n.endswith(".exe")) == ["runtime/v20/TiaMcpServer.exe", "runtime/v21/TiaMcpServer.exe"]
metadata = json.loads(files["manifest/defects-build.json"].decode("utf-8-sig"))
for row in metadata["runtimeFiles"]:
    assert hashlib.sha256(files[row["path"]]).hexdigest() == row["sha256"], row["path"]
for major in (20, 21):
    assert "COMPLETE: 28 passed" in (root / f"bin-build/defects-final-http-v{major}.log").read_text(encoding="utf-8", errors="replace")
    assert "18 HMI traversal assertions, 0 failed" in (root / f"bin-build/defects-final-hmi-v{major}.log").read_text(encoding="utf-8", errors="replace")
assert "416 passed, 0 failed, 0 skipped" in (out / "offline.log").read_text(encoding="utf-8", errors="replace")

# Keep the legacy paths used by older bundled manuals and configuration examples.
# Copy only the verified runtime payload, never local bin/obj build directories.
for name, data in list(files.items()):
    for major, folder in ((20, "bin-v20"), (21, "bin")):
        prefix = f"runtime/v{major}/"
        if name.startswith(prefix):
            files[f"tools/tiaportal-mcp/src/TiaMcpServer/{folder}/Release/net48/" + name[len(prefix):]] = data

files["RELEASE_STATUS.txt"] = (
    "Release build 2.7.2-asckye.6-defects.1; FileVersion 2.7.2.6.\r\n"
    f"Source commit: {source_commit}\r\n"
    "Run runtime/v20 or runtime/v21 on a machine with matching TIA/Openness and .NET Framework 4.8.\r\n"
    "Preserve the existing HTTP launch configuration and secret. No user secret is bundled.\r\n"
    "Offline 416 passed; final V20/V21 EXE HTTP 28 each, recursive HMI 18 each.\r\n"
    "Not deployed. Real V20/V21 project acceptance and archive retrieval have NOT been performed.\r\n"
    "See docs/releases/v2.7.2-asckye.6-defects.1.md for fixes, limitations and live acceptance steps.\r\n"
).encode("utf-8")
files["manifest/release-file-hashes.json"] = json.dumps({
    "baselineCommit": metadata["baselineCommit"], "sourceCommit": source_commit,
    "sourceStatus": "Committed source; real TIA acceptance has not been performed",
    "files": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())}
}, ensure_ascii=False, indent=2).encode("utf-8")
archive = out / (package + ".zip")
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name, data in sorted(files.items()):
        z.writestr(package + "/" + name, data)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for name, data in files.items():
        assert z.read(package + "/" + name) == data
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(".sha256").write_text(digest + "  " + archive.name + "\n", encoding="ascii")
result = {"path": str(archive), "size": archive.stat().st_size, "sha256": digest, "files": len(files), "sourceCommit": source_commit}
(out / "package-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
