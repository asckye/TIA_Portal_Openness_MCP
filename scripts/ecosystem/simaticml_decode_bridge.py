"""Bounded read-only adapter for the pinned MIT SimaticML decoder; never invokes its CLI."""
import json
from pathlib import Path
import sys
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/third-party/simaticml-decoder/src'))
from simaticml_decoder import emit, fold, parse  # noqa: E402
from simaticml_decoder.input_policy import read_xml  # noqa: E402


def decode(path):
    source = Path(path)
    if not source.is_absolute() or source.suffix.lower() != '.xml':
        raise ValueError('An absolute exported XML file is required.')
    # read_xml applies upstream byte, entity, depth and element-count limits.
    text = read_xml(source)
    tree = ET.fromstring(text)
    blocks = [e for e in tree if e.tag.startswith('SW.Blocks.')]
    if tree.tag != 'Document' or len(blocks) != 1:
        raise ValueError('Exactly one PLC block in a Document is required.')
    doc = parse.parse_document(text)
    if doc.engineering_version != 'V21':
        raise ValueError('Only exports explicitly declaring Engineering version V21 are accepted.')
    if doc.block.kind not in ('FC', 'FB'):
        raise ValueError('Decode scope is FC/FB only; use the project XML tools for other artifacts.')
    result = fold.fold_block(doc)
    warnings = list(result.warnings)
    for net in result.networks:
        warnings.extend(net.warnings)
    return {'success': True, 'analysisOnly': True, 'reimportable': False,
            'nativeTiaExecuted': False, 'nativeFormatQualified': False,
            'dataComplete': False, 'hasWarnings': bool(warnings), 'warnings': warnings,
            'readableScl': emit.emit_scl(result), 'metadata': emit.emit_sidecar(result),
            'upstreamCommit': '2f100023c2d6f4e7a4df02cbd04975de48f5d207',
            'scope': 'Readability-first interpretation, not recompilable source. Unsupported constructs and upstream qualification gaps remain; never import this output.'}


def main():
    try:
        raw = sys.stdin.read(32769)
        if len(raw) > 32768:
            raise ValueError('Request too large.')
        request = json.loads(raw)
        if set(request) != {'filePath'} or not isinstance(request['filePath'], str):
            raise ValueError('Exactly one string filePath is required.')
        payload = json.dumps(decode(request['filePath']), ensure_ascii=True)
        if len(payload) > 900000:
            raise ValueError('Decoded output exceeds response budget; split the export before analysis.')
        print(payload)
    except Exception as error:
        print(json.dumps({'success': False, 'analysisOnly': True, 'nativeTiaExecuted': False,
                          'error': str(error)[:2000]}))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
