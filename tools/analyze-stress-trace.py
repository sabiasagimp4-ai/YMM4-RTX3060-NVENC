"""Gate real GUI playback evidence, independently of clicking a button or merely seeing a window."""
import argparse
import bisect
import collections
import importlib.util
import json
from pathlib import Path


def analyze(path, projects=None):
    spec = importlib.util.spec_from_file_location('cache_trace', Path(__file__).with_name('analyze-cache-trace.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    report = module.analyze(path, 3_000_000)
    markers, updates = [], []
    with Path(path).open(encoding='utf-8-sig') as stream:
        for line in stream:
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue  # The base analyzer already flags damaged/truncated records as incomplete.
            if row.get('Stage') == 'scenario':
                markers.append((row['StartTicks'], row.get('Component', '')))
            elif row.get('Stage') == 'timeline-update':
                updates.append(row)
    markers.sort()
    ticks = [t for t, _ in markers]
    phases = collections.defaultdict(list)
    for row in updates:
        index = bisect.bisect_right(ticks, row['StartTicks']) - 1
        if index >= 0:
            phases[markers[index][1]].append(row)
    results = {}
    errors = []
    for phase in ('off-playback', 'cold-playback', 'warm-playback'):
        playing = [row for row in phases[phase] if row.get('Usage') == 'Playing' and row.get('FrameTimeTicks') is not None]
        times = [row['FrameTimeTicks'] / 10_000_000 for row in playing]
        unique = len(set(times))
        results[phase] = dict(Updates=len(playing), UniqueTimes=unique, StartSeconds=min(times, default=0),
                             EndSeconds=max(times, default=0), Routes=dict(collections.Counter(row.get('Outcome') for row in playing)))
        if unique < 20 or max(times, default=0) - min(times, default=0) < 20:
            errors.append(f'{phase}: fewer than 20 distinct times or less than 20 s of actual playback progression')
    for phase in ('stress-seek', 'stress-delete', 'stress-undo', 'stress-redo', 'stress-purge'):
        if not any(name == phase for _, name in markers):
            errors.append(f'Scenario marker missing: {phase}')
    if not report['Complete']:
        errors.append('Trace dropped/open/truncated/invalid records; timing coverage is incomplete')
    if any(row.get('Outcome') == 'exception' for row in updates):
        errors.append('Timeline update threw an exception')
    if projects is not None:
        counts = {}
        for name, expected in [('stress-delete', 420), ('stress-undo', 421), ('stress-redo', 420), ('stress-30s', 421)]:
            saved = Path(projects) / (name + '.ymmp')
            if not saved.exists():
                errors.append(f'Saved project missing: {name}')
                continue
            project = json.loads(saved.read_text(encoding='utf-8-sig'))
            timelines = project.get('Timelines', [])
            count = sum(len(t.get('Items', [])) for t in timelines)
            counts[name] = count
            if count != expected:
                errors.append(f'{name}: expected {expected} saved items, got {count}')
        report['EditEvidence'] = counts
    report['PlaybackEvidence'] = results
    report['StressErrors'] = errors
    report['StressPassed'] = not errors
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('trace')
    parser.add_argument('--output', required=True)
    parser.add_argument('--projects', help='Directory containing projects saved by real YMM4 after edits')
    args = parser.parse_args()
    result = analyze(args.trace, args.projects)
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'PlaybackEvidence': result['PlaybackEvidence'], 'StressErrors': result['StressErrors']}, ensure_ascii=False))
    raise SystemExit(0 if result['StressPassed'] else 1)
