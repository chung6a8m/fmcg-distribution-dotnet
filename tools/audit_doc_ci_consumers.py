"""Read-only, source-bound inventory. Reports leads, never selects a CI route."""
import argparse
import ast
from collections import defaultdict
import json
from pathlib import Path
import re
import subprocess


MAX_BLOB = 2 * 1024 * 1024
MAX_TOTAL = 64 * 1024 * 1024
CANDIDATES = {'docs/status/app-05-status.md', 'docs/receipts/app-05-receipts.md'}
AUDIT_FILES = {'tools/audit_doc_ci_consumers.py', 'tests/ci/test_doc_ci_consumer_audit.py'}
PATTERNS = {
    'docs-or-markdown': r'\bdocs\b|\.md\b|markdown',
    'read-or-hash': r'ReadAll(?:Text|Bytes|Lines)|Get-Content|readFile(?:Sync)?|read_(?:text|bytes)|open\s*\(|sha256|Get-FileHash|HashData|hashlib',
    'scan-or-glob': r'EnumerateFiles|EnumerateDirectories|GetFiles|Get-ChildItem|readdir|rglob|\bglob\b|os\.walk|scandir|SearchOption|\*\*/',
    'import-copy-build': r'@import|Copy-Item|CopyToOutputDirectory|CopyToPublishDirectory|\bCOPY\b|\binclude\s*=|\bcontent\s+include|\bcp\s|\bcopy\s|\binclude\s*:',
    'authority-evidence': r'acceptance|fixture|oracle|manifest|authority|validator|evidence|business\.lock|AGENTS\.md',
}


def git(repo, *args, data=None):
    return subprocess.run(['git', '--no-pager', '--no-replace-objects', *args],
                          cwd=repo, input=data, capture_output=True, check=True,
                          timeout=90).stdout


def inventory(repo, ref):
    commit = git(repo, 'rev-parse', '--verify', ref + '^{commit}').decode().strip()
    tree = git(repo, 'rev-parse', commit + '^{tree}').decode().strip()
    rows = []
    for record in git(repo, 'ls-tree', '-r', '-l', '-z', '--full-tree', commit).split(b'\0'):
        if not record:
            continue
        meta, path = record.split(b'\t', 1)
        mode, kind, oid, size = meta.decode('ascii').split()
        rows.append({'path': path.decode('utf-8', errors='surrogateescape'),
                     'mode': mode, 'kind': kind, 'oid': oid,
                     'size': int(size) if size != '-' else None})
    selected = [r for r in rows if r['kind'] == 'blob' and r['mode'] in ('100644', '100755')
                and r['size'] <= MAX_BLOB]
    unique = {r['oid']: r['size'] for r in selected}
    if sum(unique.values()) > MAX_TOTAL:
        raise ValueError('Audit blob budget exceeded; no partial result')
    raw = git(repo, 'cat-file', '--batch', data=''.join(oid + '\n' for oid in unique).encode('ascii'))
    decoded = {}
    pos = 0
    for oid, size in unique.items():
        end = raw.index(b'\n', pos)
        if raw[pos:end] != f'{oid} blob {size}'.encode('ascii'):
            raise ValueError('Unexpected Git blob header')
        pos = end + 1
        blob = raw[pos:pos + size]
        pos += size
        if len(blob) != size or raw[pos:pos + 1] != b'\n':
            raise ValueError('Incomplete Git blob')
        pos += 1
        try:
            decoded[oid] = None if b'\0' in blob else blob.decode('utf-8-sig')
        except UnicodeDecodeError:
            decoded[oid] = None
    if pos != len(raw):
        raise ValueError('Unexpected trailing Git data')
    contents = {r['path']: decoded[r['oid']] for r in selected if decoded[r['oid']] is not None}
    opaque = [{**r, 'reason': 'nonregular_or_size_limit' if r['oid'] not in decoded else 'binary_or_non_utf8'}
              for r in rows if r['path'] not in contents]
    classifier = contents.get('tools/classify_app00a_pr.py')
    allowlist = []
    if classifier is not None:
        assignments = [n.value for n in ast.parse(classifier).body if isinstance(n, ast.Assign)
                       and any(isinstance(t, ast.Name) and t.id == 'ALLOWED_DOC_PATHS' for t in n.targets)]
        if len(assignments) != 1 or not isinstance(assignments[0], ast.Call) or len(assignments[0].args) != 1:
            raise ValueError('Unrecognized classifier allowlist; manual audit required')
        allowlist = sorted(ast.literal_eval(assignments[0].args[0]))
    md = sorted({r['path'] for r in rows if r['path'].lower().endswith('.md')} | set(allowlist) | CANDIDATES)
    by_name = defaultdict(list)
    for path in md:
        by_name[path.rsplit('/', 1)[-1].lower()].append(path)
    names = re.compile(r'(?<![\w.-])(?:' + '|'.join(re.escape(n) for n in sorted(by_name, key=len, reverse=True)) + r')(?![\w.-])', re.I)
    rules = {name: re.compile(pattern, re.I) for name, pattern in PATTERNS.items()}
    refs = defaultdict(set)
    leads = []
    for source, body in sorted(contents.items()):
        role = 'audit-only' if source in AUDIT_FILES else ('prose-link-or-authority' if source.lower().endswith('.md') else 'possible-execution')
        categories = defaultdict(list)
        for n, line in enumerate(body.splitlines(), 1):
            normalized = line.replace('\\', '/')
            for match in names.finditer(normalized):
                paths = by_name[match.group().lower()]
                exact = [p for p in paths if p.lower() in normalized.lower() and '/' in p]
                for target in paths:
                    if target != source:
                        refs[target].add((source, n, role, 'literal-path' if target in exact else 'basename-lead'))
            if role == 'possible-execution':
                for label, pattern in rules.items():
                    if pattern.search(line):
                        categories[label].append(n)
        if categories:
            leads.append({'path': source, 'lines': dict(categories)})
    tracked = {r['path']: r for r in rows}
    matrix = [{'path': p, 'tracked': p in tracked,
               'mode': tracked.get(p, {}).get('mode'),
               'allowlisted': p in allowlist,
               'disposition': 'ACTIVE_POLICY_REVIEW_REQUIRED' if p in allowlist else
                              ('CANDIDATE_FULL_PENDING_REVIEW' if p in CANDIDATES else 'FULL_DEFAULT'),
               'references': [{'source': s, 'line': n, 'role': role, 'kind': kind}
                              for s, n, role, kind in sorted(refs[p])]} for p in md]
    return {'schema': 2, 'commit': commit, 'tree': tree, 'tracked_count': len(rows),
            'text_count': len(contents), 'opaque': opaque, 'active_allowlist': allowlist,
            'tracked_inventory': rows, 'markdown_matrix': matrix, 'generic_consumer_leads': leads,
            'limitations': ['Lexical leads are not proof of absence; inspect generic/dynamic consumers and opaque files.',
                            'Basename leads can be ambiguous; prose references include instructions and links.',
                            'No path is authorized for Light by this report.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ref', default='HEAD')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = inventory(Path.cwd(), args.ref)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=True, indent=2) + '\n', encoding='utf-8')
    print(f"DOC-CI audit {report['commit']} tree {report['tree']}: {report['tracked_count']} tracked, "
          f"{report['text_count']} text, {len(report['opaque'])} opaque, "
          f"{len(report['active_allowlist'])} active policy paths; no Light authorization")


if __name__ == '__main__':
    main()
