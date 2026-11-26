import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

path = Path(__file__).resolve().parents[2] / 'tools' / 'audit_doc_ci_consumers.py'
if not path.exists():
    path = Path(__file__).with_name('audit_doc_ci_consumers.py')
spec = importlib.util.spec_from_file_location('doc_ci_audit', path)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class ConsumerAuditTests(unittest.TestCase):
    def test_committed_tree_direct_dynamic_opaque_and_untracked(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            def git(*args):
                return subprocess.run(['git', *args], cwd=root, check=True, capture_output=True)
            git('init', '-q')
            git('config', 'user.name', 'Audit test')
            git('config', 'user.email', 'audit@example.invalid')
            files = {
                'tools/classify_app00a_pr.py': b'ALLOWED_DOC_PATHS = frozenset({"README.md"})\n',
                'README.md': b'Link to [status](docs/status/app-05-status.md)\n',
                'docs/status/app-05-status.md': b'not an authority\n',
                'src/reader.cs': b'File.ReadAllText(Path.Combine(root, "docs", "status", "app-05-status.md"));\n',
                'tools/scan.py': b'for p in Path(root).rglob("*.md"): pass\n',
                'docs/binary.bin': b'\x00\xff',
            }
            for name, body in files.items():
                dest = root / name
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(body)
            git('add', '.')
            git('commit', '-qm', 'fixture')
            (root / 'untracked.md').write_text('ignore')
            (root / 'src/reader.cs').write_text('uncommitted')
            report = audit.inventory(root, 'HEAD')
            self.assertEqual(report['tracked_count'], len(files))
            self.assertEqual(report['v1_allowlist'], ['README.md'])
            self.assertEqual([r['path'] for r in report['opaque']], ['docs/binary.bin'])
            item = next(r for r in report['markdown_matrix'] if r['path'] == 'docs/status/app-05-status.md')
            self.assertEqual(item['disposition'], 'CANDIDATE_FULL_PENDING_REVIEW')
            self.assertTrue(any(r['source'] == 'src/reader.cs' and r['role'] == 'possible-execution' for r in item['references']))
            self.assertTrue(any(r['source'] == 'README.md' and r['role'] == 'prose-link-or-authority' for r in item['references']))
            self.assertTrue(any(r['path'] == 'tools/scan.py' and 'scan-or-glob' in r['lines'] for r in report['generic_consumer_leads']))
            self.assertFalse(any(r['path'] == 'untracked.md' for r in report['tracked_inventory']))
            self.assertEqual(report['tree'], git('rev-parse', 'HEAD^{tree}').stdout.decode().strip())

    def test_missing_ref_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            subprocess.run(['git', 'init', '-q'], cwd=folder, check=True)
            with self.assertRaises(subprocess.CalledProcessError):
                audit.inventory(folder, 'does-not-exist')


if __name__ == '__main__':
    unittest.main()
