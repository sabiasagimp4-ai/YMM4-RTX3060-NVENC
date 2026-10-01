import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('stress_trace', Path(__file__).resolve().parents[2] / 'tools' / 'analyze-stress-trace.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class StressEvidenceTests(unittest.TestCase):
    def report(self, progress=True, dropped=0):
        rows = [{'Kind': 'session', 'StopwatchFrequency': 1000, 'Scenario': 'startup'}]
        phases = ['off-playback', 'cold-playback', 'warm-playback', 'stress-seek', 'stress-delete', 'stress-undo', 'stress-redo', 'stress-purge']
        for index, phase in enumerate(phases):
            start = index * 100_000
            rows.append(dict(Kind='span', Id=start, Stage='scenario', StartTicks=start, EndTicks=start, Component=phase, Category='marker'))
            if 'playback' in phase:
                for frame in range(30):
                    rows.append(dict(Kind='span', Id=start+frame+1, Stage='timeline-update', StartTicks=start+frame*1000+1,
                                     EndTicks=start+frame*1000+10, Category='cpu-wall', Usage='Playing', Outcome='render',
                                     OperationId=start+frame+1, FrameTimeTicks=frame*10_000_000 if progress else 0))
        rows.append(dict(Kind='summary', Dropped=dropped, OpenSpans=0))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'trace.jsonl'
            path.write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')
            return module.analyze(path)

    def test_real_progress_is_required(self):
        self.assertTrue(self.report()['StressPassed'])
        self.assertFalse(self.report(progress=False)['StressPassed'])

    def test_drops_invalidate_coverage(self):
        self.assertFalse(self.report(dropped=1)['StressPassed'])


if __name__ == '__main__':
    unittest.main()
