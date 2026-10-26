"""Check extracted Actions shell without running builds or renders."""
import ast
from pathlib import Path
import re
import subprocess
import unittest

WORKFLOW = Path(__file__).resolve().parents[6]/'.github/workflows/render-fframes-film.yml'


def run_blocks(source):
    """Read this workflow's literal run blocks, preserving shell indentation."""
    lines = source.splitlines(keepends=True)
    blocks = []
    for index, line in enumerate(lines):
        match = re.fullmatch(r'( +)run: \|\s*\n', line)
        if not match:
            continue
        prefix = match[1] + '  '
        body = []
        for following in lines[index + 1:]:
            if following.strip() and not following.startswith(prefix):
                break
            body.append(following[len(prefix):] if following.strip() else '\n')
        blocks.append(''.join(body))
    return blocks


class WorkflowTests(unittest.TestCase):
    def test_extracted_run_blocks_pass_bash_syntax(self):
        blocks = run_blocks(WORKFLOW.read_text())
        self.assertGreaterEqual(len(blocks), 3)
        for block in blocks:
            result = subprocess.run(['bash', '-n'], input=block, text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_embedded_python_is_unindented_and_parseable(self):
        blocks = run_blocks(WORKFLOW.read_text())
        bodies = [body for block in blocks for body in
                  re.findall(r"(?ms)^ *python3 - <<'PY'\n(.*?)^PY$", block)]
        self.assertEqual(len(bodies), sum(block.count("<<'PY'") for block in blocks))
        self.assertGreaterEqual(len(bodies), 2)
        for body in bodies:
            ast.parse(body)

    def test_indented_terminator_fails_after_extraction(self):
        source = "        run: |\n          if true; then\n            python3 - <<'PY'\n            pass\n            PY\n          fi\n"
        block, = run_blocks(source)
        self.assertIn('\n  PY\n', block)
        result = subprocess.run(['bash', '-n'], input=block, text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)


if __name__ == '__main__':
    unittest.main()
