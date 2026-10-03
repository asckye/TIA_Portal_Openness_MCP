#!/usr/bin/env python3
"""Production wiring assertions only; never load Siemens or call a native importer."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "tools/tiaportal-mcp/src/TiaMcpServer"
LOGIC = SRC.parent / "TiaMcp.Logic"
PORTAL = (SRC / "Siemens/Portal/Portal.Blocks.cs").read_text(encoding='utf-8')
MCP = (SRC / "ModelContextProtocol/Tools/PlcBlocksTools.cs").read_text(encoding='utf-8')
SHARED = (SRC / "ModelContextProtocol/Tools/McpServer.Blocks.cs").read_text(encoding='utf-8')
BATCH = PORTAL.split("public ResponseImportBatch ImportBlocksFromDirectory", 1)[1].split("public bool ImportType", 1)[0]
PROGRAM = MCP.split("public ResponsePlcProgramImport ImportPlcProgramFromDirectory", 1)[1].split('[McpServerTool(Name = "CompileAndDiagnosePlc")', 1)[0]

class ImportSelectionWiring(unittest.TestCase):
    def test_native_overwrite_flag(self):
        self.assertIn("group.Blocks.Import(fi, overwrite ? ImportOptions.Override : ImportOptions.None)", BATCH)
        self.assertNotIn("group.Blocks.Find", BATCH)
        self.assertNotIn("false=Rename", MCP)
        self.assertIn('bool overwrite = true', BATCH)

    def test_reject_before_every_native_action_and_dry_run(self):
        gate = PROGRAM.index("if (conflicts.Count > 0)")
        returned = PROGRAM.index("return BuildPlcProgramImportResponse", gate)
        for action in ("_session.ImportType", "_session.ImportPlcTagTable", "_session.ImportTechnologyObject", "_session.ImportBlock", "_session.CompileSoftware", "if (dryRun)"):
            self.assertLess(returned, PROGRAM.index(action))
        self.assertNotIn(".GroupBy(", PROGRAM)
        self.assertIn('x => x.Kind, x => x.ObjectName', PROGRAM)

    def test_relative_bounded_diagnostics(self):
        self.assertIn('x => x.File.Substring(sourceRoot.Length)', PROGRAM)
        self.assertIn('conflicts.Take(16)', PROGRAM)
        self.assertIn('conflictCount={conflicts.Count}', PROGRAM)
        self.assertIn('truncated=true', PROGRAM)

    def test_filters_and_defaults_preserved(self):
        self.assertIn('regex.IsMatch(Path.GetFileNameWithoutExtension(f))', PROGRAM)
        for default in ('bool compileAfter = true', 'bool stopOnImportFailure = false', 'bool dryRun = false'):
            self.assertIn(default, PROGRAM)

    def test_ordering_honest_and_deterministic(self):
        self.assertNotIn('correct dependency order', MCP)
        self.assertIn('["dependencyResolution"] = false', SHARED)
        self.assertIn('["importOrdering"] = ImportSelectionPolicy.OrderingDescription', SHARED)
        self.assertEqual(PROGRAM.count('.ThenBy(x => x.File, StringComparer.Ordinal)'), 5)

    def test_both_production_projects_reference_policy_library(self):
        self.assertTrue((LOGIC / 'ModelContextProtocol/Tools/ImportSelectionPolicy.cs').exists())
        self.assertFalse((SRC / 'ModelContextProtocol/Tools/ImportSelectionPolicy.cs').exists())
        for version in (20, 21):
            project = (SRC / f'TiaMcpServer.V{version}.csproj').read_text(encoding='utf-8')
            self.assertIn('<ProjectReference Include="../TiaMcp.Logic/TiaMcp.Logic.csproj"', project)

if __name__ == '__main__':
    unittest.main()
