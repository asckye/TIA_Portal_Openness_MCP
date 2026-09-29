"""JSON/stdio adapter to the pinned MIT PLC Tools workspace (separate process).

catalog/help never invoke a command callback. run preserves upstream exit status.
This is an execution tool: test/config files can contain executable Python, and
sim/sup/net/trace commands may communicate with equipment. No shell is used.
"""
from __future__ import annotations

import contextlib
import importlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tools/third-party/siemens-plc-tools"
GROUPS = {"code": "plc_code", "iol": "plc_iol", "net": "plc_net",
          "sim": "plc_sim", "sup": "plc_sup", "trace": "plc_trace"}


def load_groups():
    import click
    # Prefer the pinned source, while dependencies come from the chosen venv.
    sys.path[:0] = [str(SOURCE / "src")] + [str(p / "src") for p in sorted((SOURCE / "packages").iterdir()) if (p / "src").is_dir()]
    root = click.Group("plc")
    failures = {}
    for name, module in GROUPS.items():
        try:
            group = getattr(importlib.import_module(module + ".cli"), name + "_group")
            root.add_command(group, name)
        except Exception as exc:
            failures[name] = f"{type(exc).__name__}: {exc}"
    return root, failures


def catalog(command, path=()):
    import click
    row = {"command": list(path), "help": command.help or "", "options": [
        {"name": p.name, "options": p.opts, "required": p.required,
         "type": str(p.type), "default": str(p.default) if p.default is not None else None}
        for p in command.params]}
    rows = [row] if path else []
    if isinstance(command, click.Group):
        for name, child in sorted(command.commands.items()):
            rows.extend(catalog(child, (*path, name)))
    return rows


def main():
    import click
    request = json.load(sys.stdin)
    mode = request.get("mode", "catalog")
    if mode == "audit-pdf":
        # Independent report renderer, not part of the upstream command set.
        # ReportLab avoids requiring a machine-wide TeX installation for this report.
        import io
        from html import escape
        from reportlab.lib import colors
        from reportlab.lib.styles import getSampleStyleSheet
        from reportlab.pdfbase import pdfmetrics
        from reportlab.pdfbase.cidfonts import UnicodeCIDFont
        from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
        output = Path(request["outputPath"])
        if not output.is_absolute() or output.suffix.lower() != ".pdf" or output.exists():
            raise ValueError("A new absolute PDF path is required")
        report = request["report"]
        if not isinstance(report, dict) or not isinstance(report.get("findings"), list):
            raise ValueError("An engineering audit JSON report is required")
        pdfmetrics.registerFont(UnicodeCIDFont("STSong-Light"))
        styles = getSampleStyleSheet()
        for style in styles.byName.values():
            style.fontName = "STSong-Light"
        story = [Paragraph("Engineering quality report", styles["Title"]), Spacer(1, 14)]
        for key in ("filesFound", "filesInspected", "blocksInspected", "totalFindings", "dataComplete", "qualityPassed", "scope"):
            story.extend([Paragraph(escape(f"{key}: {report.get(key)}"), styles["BodyText"]), Spacer(1, 6)])
        for section in ("findings", "failures", "rules"):
            story.append(Paragraph(section, styles["Heading2"]))
            for item in report.get(section, []):
                story.extend([Paragraph(escape(json.dumps(item, ensure_ascii=False)).replace("\n", "<br/>"), styles["BodyText"]), Spacer(1, 6)])
        buffer = io.BytesIO()
        SimpleDocTemplate(buffer, title="Engineering quality report", author="TIA MCP").build(story)
        with output.open("xb") as stream:
            stream.write(buffer.getvalue())
        print(json.dumps({"success": True, "path": str(output), "bytes": len(buffer.getvalue())}))
        return 0
    args = request.get("arguments", [])
    if not isinstance(args, list) or not all(isinstance(a, str) and "\x00" not in a for a in args):
        raise ValueError("arguments must be a JSON array of strings")
    root, failures = load_groups()
    if mode == "catalog":
        print(json.dumps({"commands": catalog(root), "unavailable": failures,
                          "source": str(SOURCE), "complete": not failures}, ensure_ascii=False))
        return 0 if not failures else 2
    if not args or args[0] not in GROUPS:
        raise ValueError("Select code, iol, net, sim, sup or trace")
    if args[0] in failures:
        raise RuntimeError(f"{args[0]} unavailable: {failures[args[0]]}")
    if mode == "help":
        command = root
        for name in args:
            if not isinstance(command, click.Group) or name not in command.commands:
                raise ValueError(f"Unknown command path: {args}")
            command = command.commands[name]
        print(command.get_help(click.Context(command, info_name="plc " + " ".join(args))))
        return 0
    if mode != "run":
        raise ValueError("mode must be catalog, help or run")
    # CLI behavior and exit codes are unchanged; errors are not converted to success.
    result = root.main(args=args, prog_name="plc", standalone_mode=False)
    return result if isinstance(result, int) else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SystemExit:
        raise
    except Exception as error:
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(getattr(error, "exit_code", 1))
