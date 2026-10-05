#!/usr/bin/env python3
"""YMM4 version compatibility: merges CI results into docs/compat/ymm4-versions.json and renders the tables.

Inputs per version (written by .github/workflows/ymm4-compat.yml into one results directory):
  <version>.scan.json   tools/HostFingerprint scan: runtime, plugin references, known build, HostContracts verdict
  <version>.start.json  tools/compat/start-check.ps1: YMM4 started with the plugin, and what the plugin enabled
  <version>.tests.json  {"build": ..., "keys": ..., "probe": ...}: the plugin and its checks built against that version

Usage:
  ymm4_compat.py merge <results dir> <data.json> --plugin <version> --commit <sha> --run <url> [--date YYYY-MM-DD]
  ymm4_compat.py render <data.json> <README.md> <docs/YMM4_VERSIONS.md>
  ymm4_compat.py plan <data.json> <server versions file> --plugin <version> [--mode new|stale|all|<v> <v>...]
  ymm4_compat.py runtime-ok <version.scan.json>   (exit 0 when the .NET runtime of that YMM4 can load the plugin)
  ymm4_compat.py loadable <results dir>     (versions whose scan allows a start check, one per line)
  ymm4_compat.py issue <data.json> <version>  (a Markdown report for one version)
"""
import argparse
import datetime
import json
import os
import re
import sys

BEGIN = '<!-- ymm4-versions:begin -->'
END = '<!-- ymm4-versions:end -->'

# The cache features the plugin reports (HostIntegration.ReportStatus), in the order they are listed.
FEATURES = [
    ('preview', 'プレビュー'),
    ('selectionRects', '選択枠'),
    ('wrappedSources', '動画などの完成判定'),
    ('rulerBars', 'キャッシュバー'),
    ('simpleTachie', 'シンプル立ち絵'),
    ('lipSync', '口パクの完成判定'),
    ('animationTachie', '動く立ち絵'),
    ('psdTachie', 'PSD 立ち絵'),
]
DECODERS = [
    ('YukkuriMovieMaker.Plugin.FileSource.FFmpeg', 'FFmpeg の動画'),
    ('YukkuriMovieMaker.Plugin.FileSource.MediaFoundation', 'Media Foundation の動画'),
    ('YukkuriMovieMaker.Plugin.FileSource.WIC', 'WIC の画像'),
]
CONTRACT_NAMES = {
    'core': '描画キャッシュ本体', 'preview': 'プレビュー', 'selection-rects': '選択枠', 'wrapped-sources': '動画などの完成判定',
    'ruler-bars': 'キャッシュバー', 'simple-tachie': 'シンプル立ち絵', 'lip-sync-readiness': '口パクの完成判定',
    'animation-tachie': '動く立ち絵', 'psd-tachie': 'PSD 立ち絵',
    **{'decoder:' + assembly: name for assembly, name in DECODERS},
}


def version_key(version):
    return tuple(int(part) for part in version.split('.'))


def major(version):
    match = re.match(r'(\d+)', version or '')
    return int(match.group(1)) if match else None


def runtime_check(scan):
    """(ok, reason): can the .NET runtime this YMM4 starts load the plugin's target framework."""
    runtime = scan.get('runtime')
    plugin = (scan.get('plugin') or {}).get('framework') or ''
    needed = major(plugin.split('Version=v')[-1]) if 'Version=v' in plugin else None
    if not runtime:
        return None, 'YMM4 の実行環境の設定（runtimeconfig）がありません'
    frameworks = runtime.get('frameworks') or {}
    host = major(frameworks.get('Microsoft.NETCore.App') or frameworks.get('Microsoft.WindowsDesktop.App')
                 or (runtime.get('tfm') or '').replace('net', '', 1))
    if needed is None or host is None:
        return None, '.NET の版を読めませんでした'
    # A self-contained YMM4 runs on the runtime it carries; a framework-dependent one may roll forward.
    if host >= needed or (not runtime.get('selfContained') and (runtime.get('rollForward') or '') in ('Major', 'LatestMajor')):
        return True, ''
    return False, f'YMM4 が .NET {host} で動くため、.NET {needed} 向けのプラグインを読み込めません'


def reference_problems(scan):
    problems = []
    for reference in (scan.get('plugin') or {}).get('references') or []:
        if reference.get('host') is None:
            problems.append(f"{reference['name']} がありません")
        elif version_key(reference['host']) < version_key(reference['required']):
            problems.append(f"{reference['name']} {reference['host']}（プラグインは {reference['required']} を参照）")
    return problems


def api_note(missing):
    if not missing:
        return ''
    names = sorted({line.split(': ', 1)[-1].split(' ')[0] for line in missing})
    shown = '、'.join(f'`{name}`' for name in names[:3]) + (' など' if len(names) > 3 else '')
    return f'プラグインが使う YMM4 の API のうち {len(names)} 個がこの版にありません（{shown}）。使う場面で失敗するおそれがあります'


def judge(entry):
    """The verdict shown in the tables, from whatever was checked for this version."""
    scan, start, tests = entry.get('scan') or {}, entry.get('start'), entry.get('tests')
    verdict = {'load': 'unknown', 'nvenc': 'unknown', 'cache': 'unknown', 'missing': [], 'note': '', 'basis': None}
    if scan.get('error'):
        verdict['note'] = f"ファイルを照合できませんでした（{scan['error']}）"
        return verdict
    if scan.get('knownBuild'):
        verdict['basis'] = scan['knownBuild']
    runtime_ok, runtime_reason = runtime_check(scan)
    api = (scan.get('plugin') or {}).get('apiMissing') or []
    status = (start or {}).get('status')
    if status:
        verdict['load'] = 'partial' if api else 'ok'
        verdict['api'] = api_note(api)
        verdict['nvenc'] = 'ok' if status.get('exportHooked') else 'no'
        if not status.get('exportHooked') and status.get('exportProblem'):
            verdict['note'] = 'NVENC: ' + status['exportProblem']
        features = status.get('features')
        if status.get('cacheAvailable') and features:
            missing = [name for key, name in FEATURES if not features.get(key)]
            decoders = features.get('decoders')
            if decoders is not None:
                missing += [name for assembly, name in DECODERS if assembly not in decoders]
            verdict['cache'] = 'partial' if missing else 'full'
            verdict['missing'] = missing
            verdict['basis'] = features.get('basis') or verdict['basis']
        else:
            verdict['cache'] = 'off'
            contracts = scan.get('contracts') or {}
            off = contracts.get('off') or {}
            if 'core' in off:
                verdict['note'] = verdict['note'] or '描画キャッシュが前提とする YMM4 のコードが、確かめた版と異なります'
    elif runtime_ok is False:
        verdict.update(load='no', nvenc='no', cache='off', note=runtime_reason)
    elif start and start.get('mainWindow'):
        dialogs = ' / '.join(start.get('dialogs') or [])
        verdict.update(load='no', nvenc='no', cache='off',
                       note='YMM4 は起動しましたが、プラグインが読み込まれませんでした' + (f'（{dialogs}）' if dialogs else ''))
    else:
        problems = reference_problems(scan)
        if start:
            verdict['note'] = 'CI で YMM4 が起動しなかったため確かめられませんでした'
        elif runtime_ok is None:
            verdict['note'] = runtime_reason
        elif problems:
            verdict['note'] = '未確認（参照の版: ' + '、'.join(problems) + '）'
        else:
            verdict['note'] = '未確認'
        if api:
            verdict['api'] = api_note(api)
    if tests:
        failed = [name for name, key in (('ビルド', 'build'), ('キー検査', 'keys'), ('実ホスト検査', 'probe')) if tests.get(key) == 'failure']
        if failed:
            verdict['note'] = (verdict['note'] + '。' if verdict['note'] else '') + '検査の失敗: ' + '・'.join(failed)
    return verdict


def merge(results, data_path, plugin, commit, run, date):
    data = load(data_path)
    versions = data.setdefault('versions', {})
    found = {}
    for name in sorted(os.listdir(results)):
        match = re.match(r'^(\d+(?:\.\d+){3})\.(scan|start|tests)\.json$', name)
        if not match:
            continue
        with open(os.path.join(results, name), encoding='utf-8-sig') as stream:
            text = stream.read().strip()
        found.setdefault(match.group(1), {})[match.group(2)] = json.loads(text) if text else None
    for version, parts in found.items():
        entry = {'checked': date, 'plugin': plugin, 'commit': commit, 'run': run,
                 'scan': parts.get('scan'), 'start': parts.get('start'), 'tests': parts.get('tests')}
        previous = versions.get(version) or {}
        # A version re-checked without the Windows steps keeps the earlier start and tests of the same plugin.
        for part in ('start', 'tests'):
            if entry[part] is None and previous.get('plugin') == plugin:
                entry[part] = previous.get(part)
        entry['verdict'] = judge(entry)
        versions[version] = entry
    data['updated'] = date
    data['plugin'] = plugin
    data['versions'] = dict(sorted(versions.items(), key=lambda item: version_key(item[0]), reverse=True))
    save(data_path, data)
    print(f'merged {len(found)} versions into {data_path}')


def load(path):
    if os.path.exists(path):
        with open(path, encoding='utf-8') as stream:
            return json.load(stream)
    return {'versions': {}}


def save(path, data):
    os.makedirs(os.path.dirname(path) or '.', exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=1)
        stream.write('\n')


MARK = {'ok': '○', 'partial': '△', 'no': '×', 'unknown': '？'}


def cells(verdict):
    loaded = verdict['load'] in ('ok', 'partial')
    load = MARK[verdict['load']]
    nvenc = MARK[verdict['nvenc']] if loaded else '—'
    cache = {'full': '○', 'partial': '△', 'off': '×', 'unknown': '？'}[verdict['cache']] if loaded else '—'
    notes = []
    if verdict.get('api'):
        notes.append(verdict['api'])
    if loaded and verdict['cache'] == 'partial':
        notes.append('キャッシュで使わない機能: ' + '、'.join(verdict['missing']))
    if loaded and verdict.get('basis') and verdict['cache'] in ('full', 'partial'):
        notes.append('コードを読んだ版' if verdict['basis'] == verdict.get('version') else f"キャッシュは {verdict['basis']} と一致した部分を使用")
    if verdict['note']:
        notes.append(verdict['note'])
    return load, nvenc, cache, '。'.join(notes)


def rows(data):
    items = []
    for version, entry in data.get('versions', {}).items():
        verdict = dict(entry.get('verdict') or judge(entry), version=version)
        items.append((version, cells(verdict)))
    return sorted(items, key=lambda item: version_key(item[0]), reverse=True)


def grouped(data):
    """Consecutive versions (newest first) with the same cells, as (label, count, cells)."""
    groups = []
    for version, row in rows(data):
        if groups and groups[-1][2] == row:
            groups[-1][1].append(version)
        else:
            groups.append([version, [version], row])
    result = []
    for _, members, row in groups:
        label = members[0] if len(members) == 1 else f'{members[-1]} 〜 {members[0]}（{len(members)} 版）'
        result.append((label, row))
    return result


def table(entries):
    lines = ['| YMM4 | 読み込み | NVENC 出力 | 描画キャッシュ | 備考 |', '| --- | :---: | :---: | :---: | --- |']
    for label, (load, nvenc, cache, note) in entries:
        lines.append(f'| {label} | {load} | {nvenc} | {cache} | {note} |')
    return '\n'.join(lines)


def render(data_path, readme_path, doc_path):
    data = load(data_path)
    plugin, updated = data.get('plugin', '?'), data.get('updated', '?')
    count = len(data.get('versions', {}))
    readme_block = '\n'.join([
        BEGIN,
        f'YMM4 の更新サーバーで公開されている {count} 版を、プラグイン {plugin} で自動で確かめた結果です（{updated} 更新）。'
        '同じ結果が続く版はまとめています。版ごとの結果と確かめ方は [YMM4 の版ごとの対応](docs/YMM4_VERSIONS.md) を参照してください。',
        '',
        '**○** 使える　**△** 一部だけ使える　**×** 使えない　**？** 未確認',
        '',
        table(grouped(data)),
        END,
    ])
    with open(readme_path, encoding='utf-8') as stream:
        readme = stream.read()
    if BEGIN in readme and END in readme:
        readme = readme[:readme.index(BEGIN)] + readme_block + readme[readme.index(END) + len(END):]
    else:
        raise SystemExit(f'{readme_path}: markers {BEGIN} / {END} not found')
    with open(readme_path, 'w', encoding='utf-8', newline='\n') as stream:
        stream.write(readme)
    detail = [(f"{version}", row) for version, row in rows(data)]
    runs = sorted({entry.get('run') for entry in data.get('versions', {}).values() if entry.get('run')})
    doc = '\n'.join([
        '# YMM4 の版ごとの対応',
        '',
        f'このページは `.github/workflows/ymm4-compat.yml` が自動で書き換えます（{updated} 更新、プラグイン {plugin}）。手で編集しないでください。',
        '',
        '## 確かめ方',
        '',
        '1. **ファイルの照合（Linux、全版）**: YMM4 の更新サーバーから、YMM4 自身の更新と同じ手順で各版の DLL と実行環境の設定を取得します。'
        'YMM4 が動く .NET の版、プラグインが参照する YMM4 の DLL の版、描画キャッシュが前提とする YMM4 のコードが確かめた版と同じか（HostContracts）を調べます。',
        '2. **起動（Windows、.NET の版が合う版）**: その版の YMM4 にプラグインを入れて実際に起動し、プラグインが読み込まれたか、'
        'NVENC 出力の取消保護と描画キャッシュのどの機能を使うと判断したかを、プラグイン自身の報告で記録します。',
        '3. **検査（Windows、新しい版）**: その版に対してプラグインと検査をビルドし、キャッシュのキー検査と実ホスト検査（出力フック、画素一致）を動かします。',
        '',
        'CI のランナーには NVIDIA の GPU がないため、NVENC で実際にエンコードできるかは確かめていません。「NVENC 出力」の ○ は、YMM4 の出力に取消保護を接続できたことを表します。',
        '',
        '**○** 使える　**△** 一部だけ使える　**×** 使えない　**？** 未確認　**—** 読み込めないため対象外',
        '',
        '## 結果',
        '',
        table(detail),
        '',
        '## 実行記録',
        '',
        *[f'- {run}' for run in runs[-20:]],
        '',
    ])
    with open(doc_path, 'w', encoding='utf-8', newline='\n') as stream:
        stream.write(doc)
    print(f'rendered {count} versions into {readme_path} and {doc_path}')


def plan(data_path, server_path, plugin, mode):
    data = load(data_path)
    with open(server_path, encoding='utf-8') as stream:
        server = [line.strip() for line in stream if re.match(r'^\d+(\.\d+){3}$', line.strip())]
    known = data.get('versions', {})
    if mode == ['all']:
        chosen = server
    elif mode == ['new'] or not mode:
        chosen = [v for v in server if v not in known]
    elif mode == ['stale']:
        # Never checked, or last checked with another plugin version.
        chosen = [v for v in server if v not in known or known[v].get('plugin') != plugin]
    else:
        chosen = [v for v in mode if re.match(r'^\d+(\.\d+){3}$', v)]
    for version in sorted(set(chosen), key=version_key, reverse=True):
        print(version)


def loadable(results):
    for name in sorted(os.listdir(results)):
        match = re.match(r'^(\d+(?:\.\d+){3})\.scan\.json$', name)
        if not match:
            continue
        with open(os.path.join(results, name), encoding='utf-8-sig') as stream:
            scan = json.load(stream)
        if runtime_check(scan)[0] is not False:
            print(match.group(1))


def runtime_ok(scan_path):
    with open(scan_path, encoding='utf-8-sig') as stream:
        return runtime_check(json.load(stream))[0] is not False


def issue(data_path, version):
    entry = load(data_path)['versions'][version]
    verdict = dict(entry['verdict'], version=version)
    load_, nvenc, cache, note = cells(verdict)
    tests = entry.get('tests') or {}
    word = {'success': '成功', 'failure': '**失敗**', 'skipped': '未実行', None: '未実行'}
    print(f'YMM4 Lite **{version}** を、プラグイン {entry["plugin"]}（{entry["commit"][:7]}）で自動で確かめました。')
    print()
    print('| 項目 | 結果 |')
    print('| --- | --- |')
    print(f'| 読み込み | {load_} |')
    print(f'| NVENC 出力（取消保護の接続） | {nvenc} |')
    print(f'| 描画キャッシュ | {cache} |')
    print(f'| 備考 | {note or "—"} |')
    print(f'| この版に対するビルド・キー検査・実ホスト検査 | {word.get(tests.get("build"), tests.get("build"))}・{word.get(tests.get("keys"), tests.get("keys"))}・{word.get(tests.get("probe"), tests.get("probe"))} |')
    contracts = (entry.get('scan') or {}).get('contracts') or {}
    if contracts.get('off'):
        print()
        print('描画キャッシュで使わない機能（照合の内訳）:')
        print()
        for feature, problem in contracts['off'].items():
            print(f'- {CONTRACT_NAMES.get(feature, feature)}: {problem}')
    print()
    print('NVENC 自体の出力は GPU の無い CI では確かめていません。使えない判定や失敗があれば、この issue を Claude に渡して対応を頼んでください。')
    print()
    print(f'実行記録: {entry["run"]}')


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    m = sub.add_parser('merge')
    m.add_argument('results'); m.add_argument('data')
    m.add_argument('--plugin', required=True); m.add_argument('--commit', required=True); m.add_argument('--run', required=True)
    m.add_argument('--date', default=datetime.date.today().isoformat())
    r = sub.add_parser('render')
    r.add_argument('data'); r.add_argument('readme'); r.add_argument('doc')
    p = sub.add_parser('plan')
    p.add_argument('data'); p.add_argument('server'); p.add_argument('--plugin', required=True)
    p.add_argument('--mode', nargs='*', default=['new'])
    l = sub.add_parser('loadable')
    l.add_argument('results')
    o = sub.add_parser('runtime-ok')
    o.add_argument('scan')
    i = sub.add_parser('issue')
    i.add_argument('data'); i.add_argument('version')
    args = parser.parse_args(argv)
    if args.command == 'merge':
        merge(args.results, args.data, args.plugin, args.commit, args.run, args.date)
    elif args.command == 'render':
        render(args.data, args.readme, args.doc)
    elif args.command == 'plan':
        plan(args.data, args.server, args.plugin, args.mode)
    elif args.command == 'loadable':
        loadable(args.results)
    elif args.command == 'runtime-ok':
        sys.exit(0 if runtime_ok(args.scan) else 1)
    elif args.command == 'issue':
        issue(args.data, args.version)


if __name__ == '__main__':
    main(sys.argv[1:])
