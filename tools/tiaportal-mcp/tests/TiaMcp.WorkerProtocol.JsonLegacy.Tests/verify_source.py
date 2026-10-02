"""Fail if the reviewed source corpus changed; refresh the mirror deliberately."""
from pathlib import Path
import hashlib
source = Path(__file__).resolve().parent.parent / 'TiaMcp.WorkerProtocol.JsonV2.Tests/Program.cs'
expected = 'b8841150ca9748c6425e7d1968c94d82a78d0fec1a0d122da4c3aa32a492be29'
assert hashlib.sha256(source.read_bytes()).hexdigest() == expected, 'Reviewed strict corpus changed: reconcile the legacy mirror before accepting results.'
print('PASS: reviewed 343-check source corpus hash unchanged')
