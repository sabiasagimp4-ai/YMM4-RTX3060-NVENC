"""Windows PowerShell 5.1 reads a script without a byte order mark in the system code page (CP932 on a Japanese
Windows), so one non-ASCII character can break the parse there (tools/local-full-check.ps1 did, with an em dash).
The scripts stay ASCII: Japanese text is written as \\u escapes (tools/ci/gui-smoke.ps1, U '...')."""
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class PowerShellAsciiChecks(unittest.TestCase):
    def test_every_script_is_ascii(self):
        listed = subprocess.run(['git', 'ls-files', '*.ps1'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split()
        self.assertTrue(listed, 'no PowerShell scripts found')
        for name in listed:
            data = (ROOT / name).read_bytes()
            for number, line in enumerate(data.splitlines(), 1):
                bad = [byte for byte in line if byte > 0x7F]
                self.assertFalse(bad, f'{name}:{number}: non-ASCII byte 0x{bad[0]:02X} (Windows PowerShell 5.1 reads it in the system code page)' if bad else '')


if __name__ == '__main__':
    unittest.main()
