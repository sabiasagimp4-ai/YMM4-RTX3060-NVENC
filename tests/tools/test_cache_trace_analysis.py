import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('cache_trace_analysis', Path(__file__).resolve().parents[2] / 'tools/analyze-cache-trace.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class TraceAnalysisTests(unittest.TestCase):
    def analyze(self, spans, footer=True, limit=1_000_000):
        rows = [dict(Kind='session', StopwatchFrequency=1000, Scenario='initial'), *spans]
        if footer:
            rows.append(dict(Kind='summary', Dropped=0, OpenSpans=0))
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / 'trace.jsonl'
            path.write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')
            return module.analyze(path, limit)

    @staticmethod
    def span(stage, start, end, operation=7, **extra):
        return dict(Kind='span', Stage=stage, Category='cpu-wall', OperationId=operation,
                    StartTicks=start, EndTicks=end, Usage='Playing', **extra)

    def test_completed_root_attributes_earlier_children_and_worker(self):
        rows = [self.span('CacheRead', 10, 12), self.span('disk-worker', 30, 35),
                self.span('timeline-update', 0, 20, Outcome='disk')]
        report = self.analyze(rows)
        self.assertTrue(report['Complete'])
        self.assertEqual({'disk'}, {row['Route'] for row in report['Measurements']})
        self.assertEqual({'Playing'}, {row['Usage'] for row in report['Measurements']})
        # The worker is causally related; its time is not added to root duration.
        self.assertEqual(20, next(row['MeanMs'] for row in report['Measurements'] if row['Stage'] == 'timeline-update'))

    def test_route_groups_do_not_mix_hits_and_host_render(self):
        rows = [self.span('work', 0, 2), self.span('timeline-update', 0, 3, Outcome='ram'),
                self.span('work', 10, 30, operation=8), self.span('timeline-update', 10, 31, operation=8, Outcome='render')]
        groups = {row['Route']: row['MeanMs'] for row in self.analyze(rows)['Measurements'] if row['Stage'] == 'work'}
        self.assertEqual({'ram': 2, 'render': 20}, groups)

    def test_nearest_rank_boundary_for_twenty_samples(self):
        report = self.analyze([self.span('work', 0, n) for n in range(1, 21)])
        row = report['Measurements'][0]
        self.assertEqual((10, 19, 20), (row['P50Ms'], row['P95Ms'], row['P99Ms']))

    def test_marker_time_not_writer_order_and_coverage_deduplication(self):
        rows = [self.span('work', 20, 22),
                dict(Kind='span', Stage='scenario', Category='marker', StartTicks=10, EndTicks=10, Component='seek'),
                dict(Kind='span', Stage='processor-hook', Category='coverage', StartTicks=0, EndTicks=1,
                     Component='method-A', Outcome='unhookable', Detail='ArgumentException')]
        rows.append(rows[-1].copy())
        report = self.analyze(rows)
        self.assertEqual('seek', report['Measurements'][0]['Scenario'])
        self.assertEqual(2, report['Coverage']['unhookable'])
        self.assertEqual(1, len(report['CoverageMethods']))
        self.assertEqual(2, report['CoverageMethods'][0]['Records'])

    def test_incomplete_session_and_unrelated_span(self):
        rows = [self.span('work', 0, 1, operation=0), self.span('work', 1, 2)]
        self.assertFalse(self.analyze(rows, footer=False)['Complete'])
        self.assertFalse(self.analyze(rows, limit=1)['Complete'])
        self.assertEqual('unattributed', self.analyze(rows[:1])['Measurements'][0]['Route'])


if __name__ == '__main__':
    unittest.main()
