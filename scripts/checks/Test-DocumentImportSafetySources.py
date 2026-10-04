#!/usr/bin/env python3
"""Static production wiring guards only; no SDK/native/transactional acceptance."""
from pathlib import Path
import unittest
from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]
sources = EngineSources()
SINGLE = sources.member('ImportFromDocuments', signature='bool ImportFromDocuments')
BATCH = sources.member('ImportBlocksFromDocuments', signature='IEnumerable<PlcBlock>')
MCP = sources.member('ImportBlocksFromDocuments', tool=True)
PRIMITIVES = (ROOT / 'tools/tiaportal-mcp/src/TiaMcp.Adapters/Native/Plc/PlcDocumentPrimitives.cs').read_text(encoding='utf-8')

class DocumentImportSafetySources(unittest.TestCase):
    def test_shared_primitives_keep_each_native_operation_and_argument(self):
        for declaration in (
            'public static PlcBlockSystemGroup BlockGroup(PlcSoftware software) => software.BlockGroup;',
            'public static PlcBlockComposition Blocks(PlcBlockGroup group) => group.Blocks;',
            'public static DocumentImportResultForBlocks Import(PlcBlockComposition blocks, DirectoryInfo directory, string name, ImportDocumentOptions option) => blocks.ImportFromDocuments(directory, name, option);',
            'public static DocumentResultState State(DocumentImportResult result) => result.State;',
            'public static PlcBlockAssociation ImportedBlocks(DocumentImportResultForBlocks result) => result.ImportedPlcBlocks;',
            'public static void SetNumber(PlcBlock block, int number) => block.Number = number;',
            'public static void SetAutoNumber(PlcBlock block, bool autoNumber) => block.AutoNumber = autoNumber;',
        ):
            self.assertEqual(PRIMITIVES.count(declaration), 1)
        self.assertEqual(PRIMITIVES.count('.ImportFromDocuments('), 1)
        self.assertIn('Documents.Import(Documents.Blocks(targetGroup), dir, fileNameWithoutExtension, option)', SINGLE)
        self.assertIn('Documents.SetAutoNumber(imported, false);', SINGLE)
        self.assertLess(SINGLE.index('Documents.SetAutoNumber(imported, false);'), SINGLE.index('Documents.SetNumber(imported, prevNumber.Value);'))

    def test_worker_blocks_reads_and_previews_after_uncertain_document_batch(self):
        worker = ROOT / 'tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker'
        program = (worker / 'Program.cs').read_text(encoding='utf-8')
        guard = program.index('sessionOutcome.RequireUsable(readOnly);')
        self.assertLess(guard, program.index('MutationIdentityPolicy.ValidateTarget'))
        self.assertLess(guard, program.index('method.Invoke(engine, call)'))
        self.assertIn('if(result is PlcBatchDocumentImportResult batchDocuments && batchDocuments.RequiresSessionReset) sessionOutcome.MarkUncertain(blockReads: true);', program)
        self.assertNotIn('batchOutcomeUnknown', program)
        self.assertIn('WorkerSessionOutcomeState.cs', (worker / 'TiaMcpServer.PlcWorker.csproj').read_text(encoding='utf-8'))

    def test_missing_group_rejected_before_native_call(self):
        self.assertIn('string.IsNullOrWhiteSpace(groupPath) ? Documents.BlockGroup(plcSoftware)', BATCH)
        gate = BATCH.index('?? throw new PortalException(PortalErrorCode.NotFound')
        self.assertLess(gate, BATCH.index('Documents.Import(Documents.Blocks(group), dir, name, option)'))
        self.assertNotIn('Documents.Import(Documents.Blocks(Documents.BlockGroup(plcSoftware))', BATCH)
        self.assertNotIn('catch', BATCH[:gate])

    def test_one_native_call_site_no_retry_and_stop_for_ambiguous_state(self):
        self.assertEqual(BATCH.count('Documents.Import('), 1)
        self.assertIn('result == null || Documents.State(result) != DocumentResultState.Success || Documents.ImportedBlocks(result) == null', BATCH)
        self.assertEqual(BATCH.count('LastImportFromDocumentsStopped = true;\n                    break;'), 1)
        self.assertEqual(BATCH.count('LastImportFromDocumentsStopped = true;\n                        break;'), 1)
        self.assertNotIn('continue;', BATCH)

    def test_counts_are_document_sets_not_blocks(self):
        self.assertIn('LastImportFromDocumentsAttempted++;', BATCH)
        self.assertIn('LastImportFromDocumentsSucceeded++;', BATCH)
        self.assertLess(BATCH.index('imported.AddRange(blocks)'), BATCH.index('LastImportFromDocumentsSucceeded++;'))
        self.assertIn('selected - attempted', MCP)
        self.assertIn('!stopped && !responseReportingFailed && succeeded == selected', MCP)
        for field in ('selectedFiles', 'attemptedFiles', 'succeededFiles', 'notAttemptedFiles', 'stopped', 'mayHaveChanged'):
            self.assertIn(f'["{field}"]', MCP)

    def test_partial_results_evidence_and_no_false_rollback(self):
        self.assertIn('DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result)', BATCH)
        self.assertIn('DocumentImportedNamesSuffix(result) + DocumentMessageSuffix(result)', SINGLE)
        for text in (SINGLE, BATCH, MCP):
            self.assertIn('may have changed', text)
            self.assertNotIn('整份文档未导入', text)
            self.assertNotIn('一个都没导进去', text)
            self.assertNotIn('The document set was not imported', text)
        self.assertIn('Materialize before adding', BATCH)

    def test_deterministic_selection_before_attempt(self):
        self.assertIn('.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal)', BATCH)
        self.assertLess(BATCH.index('var selected ='), BATCH.index('LastImportFromDocumentsAttempted++;'))

    def test_post_native_reporting_keeps_evidence(self):
        self.assertLess(MCP.index('var failures = _domain.LastImportFromDocumentsFailures.ToList()'), MCP.index('Helper.GetAttributeList(block)'))
        self.assertIn('Block metadata readback failed after import:', MCP)
        self.assertIn('Progress notification failed after import:', MCP)
        self.assertIn('["responseReportingFailed"] = responseReportingFailed', MCP)
        self.assertIn('Post-import result or identity readback failed:', SINGLE)
        self.assertNotIn('McpHints.Recovery(ex)', MCP)

    def test_override_feature_preserved_but_not_none(self):
        self.assertIn('(option & ImportDocumentOptions.Override) != 0 && existing != null', SINGLE)
        self.assertIn('if (prevNumber.HasValue)', SINGLE)
        self.assertIn('Documents.SetNumber(imported, prevNumber.Value)', SINGLE)
        self.assertIn('Documents.SetAutoNumber(imported, prevAutoNumber)', SINGLE)

if __name__ == '__main__':
    unittest.main()
