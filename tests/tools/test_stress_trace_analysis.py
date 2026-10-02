import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('stress_trace', Path(__file__).resolve().parents[2] / 'tools' / 'analyze-stress-trace.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class StressEvidenceTests(unittest.TestCase):
    def report(self, progress=True, dropped=0, edit_counts=None, controls=True, damaged=False, revisit_frames=None, reuse=True):
        rows = [{'Kind': 'session', 'StopwatchFrequency': 1000, 'Scenario': 'startup'}]
        phases = ['off-playback', 'cold-playback', 'warm-playback', 'stress-seek', 'stress-delete', 'stress-undo', 'stress-redo', 'stress-purge']
        if revisit_frames is not None:
            phases.extend(['stress-revisit-1', 'stress-revisit-2'])
        for index, phase in enumerate(phases):
            start = index * 100_000
            if controls and index < 2:
                rows.append(dict(Kind='span', Id=start-1, Stage='cache-settings', StartTicks=start-1, EndTicks=start-1,
                                 Component='preview-off' if index == 0 else 'preview-on', Category='state'))
            rows.append(dict(Kind='span', Id=start, Stage='scenario', StartTicks=start, EndTicks=start, Component=phase, Category='marker'))
            if controls and phase == 'stress-purge':
                rows.append(dict(Kind='span', Id=start+1, Stage='cache-purge', StartTicks=start+1, EndTicks=start+10, Outcome='ok'))
            if 'playback' in phase:
                for frame in range(30):
                    rows.append(dict(Kind='span', Id=start+frame+1, Stage='timeline-update', StartTicks=start+frame*1000+1,
                                     EndTicks=start+frame*1000+10, Category='cpu-wall', Usage='Playing', Outcome='render',
                                     OperationId=start+frame+1, FrameTimeTicks=frame*10_000_000 if progress else 0))
            if phase.startswith('stress-revisit'):
                for frame in range(3):
                    rows.append(dict(Kind='span', Id=start+frame+1, Stage='timeline-update', StartTicks=start+frame*1000+1,
                                     EndTicks=start+frame*1000+10, Usage='Paused', Outcome='ram' if reuse else 'render',
                                     FrameTimeTicks=round(frame*10_000_000/30)))
        rows.append(dict(Kind='summary', Dropped=dropped, OpenSpans=0))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'trace.jsonl'
            path.write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')
            if damaged:
                with path.open('a') as stream:
                    stream.write('\n{"Kind":"span"')
            if edit_counts is not None:
                for name, count in edit_counts.items():
                    (Path(directory) / (name + '.ymmp')).write_text(json.dumps({'Timelines': [{'Items': [{}] * count}]}))
            if revisit_frames is not None:
                (Path(directory) / 'stress-revisit.json').write_text(json.dumps(revisit_frames))
            return module.analyze(path, directory if edit_counts is not None else None)

    def test_real_progress_is_required(self):
        self.assertTrue(self.report()['StressPassed'])
        self.assertFalse(self.report(progress=False)['StressPassed'])

    def test_drops_invalidate_coverage(self):
        self.assertFalse(self.report(dropped=1)['StressPassed'])

    def test_edit_keystrokes_without_saved_changes_are_rejected(self):
        counts = {'stress-delete': 420, 'stress-undo': 421, 'stress-redo': 420, 'stress-30s': 421}
        self.assertTrue(self.report(edit_counts=counts)['StressPassed'])
        counts['stress-delete'] = 421
        self.assertFalse(self.report(edit_counts=counts)['StressPassed'])

    def test_controls_must_actually_change_state_and_clear(self):
        counts = {'stress-delete': 420, 'stress-undo': 421, 'stress-redo': 420, 'stress-30s': 421}
        self.assertFalse(self.report(edit_counts=counts, controls=False)['StressPassed'])

    def test_truncated_trace_produces_failed_report(self):
        self.assertFalse(self.report(damaged=True)['StressPassed'])

    def test_revisit_requires_matching_times_and_actual_cache_routes(self):
        counts = {'stress-delete': 420, 'stress-undo': 421, 'stress-redo': 420, 'stress-30s': 421}
        self.assertTrue(self.report(edit_counts=counts, revisit_frames=[0, 1, 2])['StressPassed'])
        self.assertFalse(self.report(edit_counts=counts, revisit_frames=[0, 1, 2], reuse=False)['StressPassed'])
        self.assertFalse(self.report(edit_counts=counts, revisit_frames=[100, 101, 102])['StressPassed'])


if __name__ == '__main__':
    unittest.main()
