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
    markers, updates, settings, purges = [], [], [], []
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
            elif row.get('Stage') == 'cache-settings':
                settings.append(row)
            elif row.get('Stage') == 'cache-purge':
                purges.append(row)
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
        # Source clock + Playing usage proves playback even when a very heavy CI host skips most frames.
        # Ten distinct updates rules out a single stale frame; the 20-second extent remains mandatory.
        if unique < 10 or max(times, default=0) - min(times, default=0) < 20:
            errors.append(f'{phase}: fewer than 10 distinct times or less than 20 s of actual playback progression')
    for phase in ('stress-seek', 'stress-delete', 'stress-undo', 'stress-redo', 'stress-purge'):
        if not any(name == phase for _, name in markers):
            errors.append(f'Scenario marker missing: {phase}')
    if not report['Complete']:
        errors.append('Trace dropped/open/truncated/invalid records; timing coverage is incomplete')
    if any(row.get('Outcome') == 'exception' for row in updates):
        errors.append('Timeline update threw an exception')
    if projects is not None:
        revisit_file = Path(projects) / 'stress-revisit.json'
        if revisit_file.exists():
            wanted = set(json.loads(revisit_file.read_text(encoding='utf-8-sig')))
            evidence = {}
            for phase in ('stress-revisit-1', 'stress-revisit-2'):
                observed = [r for r in phases[phase] if r.get('FrameTimeTicks') is not None]
                frame = lambda r: round(r['FrameTimeTicks'] * 30 / 10_000_000)
                actual = {frame(r) for r in observed}
                cached = [r for r in observed if frame(r) in wanted and r.get('Outcome') in ('ram', 'gpu', 'disk')]
                evidence[phase] = dict(RequestedFrames=sorted(actual), ExpectedFrames=sorted(wanted),
                                       ExactRevisits=len(wanted & actual), CachedExactUpdates=len(cached),
                                       Routes=dict(collections.Counter(r.get('Outcome') for r in observed)))
                if len(wanted & actual) < 3:
                    errors.append(f'{phase}: fewer than three cold frame times actually revisited')
                if not cached:
                    errors.append(f'{phase}: no GPU/RAM/disk reuse observed on the exact cold frame times')
            report['RevisitEvidence'] = evidence
        control = {}
        for phase, expected in [('off-playback', 'preview-off'), ('cold-playback', 'preview-on'), ('warm-playback', 'preview-on')]:
            begin = next((t for t, name in markers if name == phase), -1)
            before = sorted((r for r in settings if r['StartTicks'] <= begin), key=lambda r: r['StartTicks'])
            actual = before[-1].get('Component') if before else None
            control[phase] = actual
            if actual != expected:
                errors.append(f'{phase}: preview switch state {actual!r}, expected {expected}')
        purge_start = next((t for t, name in markers if name == 'stress-purge'), -1)
        cleared = any(r['StartTicks'] >= purge_start and r.get('Outcome') == 'ok' for r in purges)
        control['PurgeCompleted'] = cleared
        if not cleared:
            errors.append('No successful purge operation after stress-purge marker')
        report['ControlEvidence'] = control
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
