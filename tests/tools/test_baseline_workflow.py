import re
import unittest
from pathlib import Path


class BaselineWorkflowChecks(unittest.TestCase):
    def test_fixed_commits_require_explicit_manual_opt_in(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/cache-development.yml').read_text()
        self.assertRegex(workflow, r'measure_baselines:\n[^\n]*\n        type: boolean\n        default: false')
        for step in re.split(r'(?=^      - )', workflow, flags=re.M):
            if re.search(r'git fetch origin [0-9a-f]{40}', step):
                self.assertIn("if: ${{ github.event_name == 'workflow_dispatch' && inputs.measure_baselines }}", step)
            for check in ['--animation-tachie-check', '--psd-tachie-check', '--simple-tachie-check', '--idle-parallel-check', '--edit-description-check']:
                if check in step:
                    self.assertNotIn('measure_baselines', step, check + ' must always run')


if __name__ == '__main__':
    unittest.main()
