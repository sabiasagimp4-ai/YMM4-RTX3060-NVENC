"""Compile the production SPS parser; no GPU or proprietary host binary is needed."""
from pathlib import Path
import subprocess
import tempfile
import unittest


class HevcConfigurationChecks(unittest.TestCase):
    def test_encoder_parameter_sets_and_invalid_input(self):
        root = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "check.cpp"
            binary = Path(folder) / "check"
            source.write_text('#include "tests/HevcConfigurationChecks.h"\n'
                              'int main() { CheckHevcConfiguration(); }\n', encoding="utf-8")
            subprocess.run(["c++", "-std=c++17", "-Wall", "-Wextra", "-Werror",
                            "-I", str(root), str(source), "-o", str(binary)],
                           check=True, capture_output=True, timeout=30)
            subprocess.run([str(binary)], check=True, capture_output=True, timeout=10)
