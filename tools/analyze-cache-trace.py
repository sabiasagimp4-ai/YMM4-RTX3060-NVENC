#!/usr/bin/env python3
"""Summarize retained JSONL trace records; inclusive spans must never be added as frame time."""
import argparse
import bisect
import collections
import json
import math
from pathlib import Path


def analyze(path, limit=1_000_000):
    session = None
    footer = None
    spans, markers = [], []
    truncated = False
    parse_errors = 0
    with Path(path).open(encoding='utf-8-sig') as stream:
        for line in stream:
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                parse_errors += 1
                continue
            if row.get('Kind') == 'session':
                session = row
            elif row.get('Kind') == 'summary':
                footer = row
            elif row.get('Kind') == 'span':
                if len(spans) < limit:
                    spans.append(row)
                    if row.get('Stage') == 'scenario':
                        markers.append((row['StartTicks'], row.get('Component') or 'unnamed'))
                else:
                    truncated = True
    if not session or session.get('StopwatchFrequency', 0) <= 0:
        raise ValueError('Trace session/frequency missing')
    frequency = session['StopwatchFrequency']
    markers.sort()
    marker_ticks = [x[0] for x in markers]
    groups = collections.defaultdict(list)
    routes = collections.Counter()
    coverage = collections.Counter()
    for row in spans:
        if row['EndTicks'] < row['StartTicks']:
            parse_errors += 1
            continue
        if row.get('Category') == 'coverage':
            coverage[row.get('Outcome', 'unknown')] += 1
            continue
        if row.get('Stage') == 'timeline-update':
            routes[row.get('Outcome', 'unknown')] += 1
        if row.get('Category') in ('marker', 'state'):
            continue
        i = bisect.bisect_right(marker_ticks, row['StartTicks']) - 1
        scenario = markers[i][1] if i >= 0 else session.get('Scenario', 'unnamed')
        key = (scenario, row['Stage'], row.get('Category', 'unknown'), row.get('Component') or '')
        groups[key].append((row['EndTicks'] - row['StartTicks']) * 1000 / frequency)
    measurements = []
    for (scenario, stage, category, component), values in groups.items():
        values.sort()
        def percentile(p):
            return values[math.ceil((len(values) - 1) * p)]
        measurements.append(dict(Scenario=scenario, Stage=stage, Category=category, Component=component,
                                 Samples=len(values), MeanMs=sum(values)/len(values),
                                 P50Ms=percentile(.5), P95Ms=percentile(.95), P99Ms=percentile(.99), MaxMs=values[-1]))
    measurements.sort(key=lambda r: r['MeanMs'], reverse=True)
    complete = bool(footer) and footer.get('Dropped', 0) == 0 and footer.get('OpenSpans', 0) == 0 and not truncated and parse_errors == 0
    return dict(Session=session, Summary=footer, Complete=complete, AnalysisTruncated=truncated,
                ParseErrors=parse_errors, RetainedSpans=len(spans), Routes=dict(routes), Coverage=dict(coverage),
                Interpretation='Inclusive CPU wall time, not CPU cycles or GPU execution time. Do not sum nested spans. '
                               'Coverage is only discovered hookable interfaces; dropped/open/missing records invalidate completeness.',
                Measurements=measurements)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('trace')
    parser.add_argument('--output')
    parser.add_argument('--limit', type=int, default=1_000_000)
    args = parser.parse_args()
    report = analyze(args.trace, args.limit)
    result = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        Path(args.output).parent.mkdir(parents=True, exist_ok=True)
        Path(args.output).write_text(result + '\n', encoding='utf-8')
    else:
        print(result)
