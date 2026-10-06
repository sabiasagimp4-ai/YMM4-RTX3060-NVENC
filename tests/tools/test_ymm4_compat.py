import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('ymm4_compat', ROOT / 'tools/compat/ymm4_compat.py')
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)

PLUGIN = {'framework': '.NETCoreApp,Version=v10.0',
          'references': [{'name': 'YukkuriMovieMaker', 'required': '4.56.1.0', 'host': '4.56.1.0'}]}


def scan(net='10.0.0', **extra):
    return {'runtime': {'tfm': 'net10.0', 'rollForward': None,
                        'frameworks': {'Microsoft.NETCore.App': net, 'Microsoft.WindowsDesktop.App': net}},
            'plugin': PLUGIN, 'contracts': {'baseline': '4.56.1.0', 'features': ['core'], 'off': {}}, **extra}


ALL_FEATURES = {'basis': '4.56.1.0', 'preview': True, 'selectionRects': True, 'wrappedSources': True, 'rulerBars': True, 'identityRandom': True,
                'simpleTachie': True, 'lipSync': True, 'animationTachie': True, 'psdTachie': True, 'decoders': None}


def started(features=ALL_FEATURES, export=True, cache=True):
    return {'mainWindow': True, 'dialogs': [], 'status': {'exportHooked': export, 'exportProblem': '' if export else 'x',
                                                         'cacheAvailable': cache, 'features': features if cache else None}}


class VerdictChecks(unittest.TestCase):
    def test_old_runtime_cannot_load_the_plugin(self):
        verdict = compat.judge({'scan': scan('8.0.0')})
        self.assertEqual(('no', 'off'), (verdict['load'], verdict['cache']))
        self.assertIn('.NET 8', verdict['note'])

    def test_self_contained_runtime_is_read_from_its_target(self):
        s = {'runtime': {'tfm': 'net9.0', 'selfContained': True, 'rollForward': 'LatestMajor', 'frameworks': {}}, 'plugin': PLUGIN}
        self.assertEqual('no', compat.judge({'scan': s})['load'])
        s['runtime']['tfm'] = 'net10.0'
        self.assertEqual('unknown', compat.judge({'scan': s})['load'])

    def test_missing_host_api_makes_a_loaded_version_partial(self):
        s = scan()
        s['plugin'] = dict(PLUGIN, apiMissing=['YukkuriMovieMaker: Y.Api::New instance Void <0>(String)'])
        verdict = compat.judge({'scan': s, 'start': started()})
        self.assertEqual('partial', verdict['load'])
        load, _, _, note = compat.cells(dict(verdict, version='4.50.0.0'))
        self.assertEqual('△', load)
        self.assertIn('`Y.Api::New`', note)

    def test_older_referenced_host_assembly_is_not_loadable(self):
        s = scan()
        s['plugin'] = dict(PLUGIN, references=[{'name': 'YukkuriMovieMaker', 'required': '4.56.1.0', 'host': '4.56.0.1'}],
                           apiMissing=['YukkuriMovieMaker: Y.Api::New instance Void <0>(String)'])
        verdict = compat.judge({'scan': s})
        self.assertEqual(('no', 'off'), (verdict['load'], verdict['cache']))
        self.assertIn('YukkuriMovieMaker 4.56.1.0 より古い', verdict['note'])
        self.assertEqual(1, verdict['referenceApi'])
        self.assertFalse(compat.static_load_ok(s))
        self.assertTrue(compat.static_load_ok(scan()))
        self.assertFalse(compat.static_load_ok({'error': 'download failed'}))

    def test_load_refusal_dialog_is_not_loadable(self):
        start = {'mainWindow': False, 'status': None, 'dialogs': [
            ' | Button: OK | Static: The required files for YMM4 could not be loaded.\r\n---\r\nYukkuriMovieMaker, Version=4.56.1.0, Culture=neutral, PublicKeyToken=null']}
        verdict = compat.judge({'scan': scan(), 'start': start})
        self.assertEqual('no', verdict['load'])
        self.assertIn('YukkuriMovieMaker, Version=4.56.1.0', verdict['note'])

    def test_members_the_plugin_guards_do_not_count_as_missing(self):
        s = scan()
        s['plugin'] = dict(PLUGIN, apiMissing=[
            'YukkuriMovieMaker.Plugin: YukkuriMovieMaker.Plugin.FileWriter.IVideoFileWriter3',
            'YukkuriMovieMaker.Plugin: YukkuriMovieMaker.Commons.ControlTagParser::Parse static System.ValueTuple`4<String>(String)'])
        self.assertEqual('ok', compat.judge({'scan': s, 'start': started()})['load'])
        rules = compat.guarded_rules()
        self.assertFalse(compat.guarded('YukkuriMovieMaker: YukkuriMovieMaker.Project.Scenes::.ctor instance Void <0>()', rules))
        self.assertTrue(compat.guarded('YukkuriMovieMaker: YukkuriMovieMaker.Project.Scenes::.ctor instance Void <0>(Boolean)', rules))

    def test_failed_scan_is_unknown(self):
        self.assertEqual('unknown', compat.judge({'scan': {'error': 'download failed'}})['load'])

    def test_roll_forward_to_a_newer_major_needs_a_start(self):
        s = scan('9.0.0')
        s['runtime']['rollForward'] = 'LatestMajor'
        self.assertEqual('unknown', compat.judge({'scan': s})['load'])

    def test_plugin_report_decides_features(self):
        self.assertEqual(('ok', 'ok', 'full'), tuple(compat.judge({'scan': scan(), 'start': started()})[k] for k in ('load', 'nvenc', 'cache')))
        partial = dict(ALL_FEATURES, psdTachie=False, decoders=['YukkuriMovieMaker.Plugin.FileSource.WIC'])
        verdict = compat.judge({'scan': scan(), 'start': started(partial)})
        self.assertEqual('partial', verdict['cache'])
        self.assertEqual(['PSD 立ち絵', 'FFmpeg の動画', 'Media Foundation の動画'], verdict['missing'])
        self.assertEqual('off', compat.judge({'scan': scan(), 'start': started(cache=False)})['cache'])
        self.assertEqual('no', compat.judge({'scan': scan(), 'start': started(export=False)})['nvenc'])

    def test_basis_names_the_builds_whose_code_was_read(self):
        self.assertEqual('コードを読んだ版', compat.basis_note('4.56.1.0', '4.56.1.0'))
        self.assertEqual('コードを読んだ版', compat.basis_note('4.47.0.0〜4.47.0.5', '4.47.0.3'))
        self.assertEqual('コードを読んだ版（4.56.1.0 と同じ部分も使用）', compat.basis_note('4.56.1.0 / 4.56.0.0〜4.56.0.1', '4.56.0.1'))
        self.assertEqual('キャッシュは 4.56.1.0 と一致した部分を使用', compat.basis_note('4.56.1.0', '4.56.0.1'))
        self.assertEqual('キャッシュは ? と一致した部分を使用', compat.basis_note('?', '4.56.0.1'))

    def test_main_window_without_report_means_not_loaded(self):
        verdict = compat.judge({'scan': scan(), 'start': {'mainWindow': True, 'dialogs': ['Error :: load failed'], 'status': None}})
        self.assertEqual('no', verdict['load'])
        self.assertIn('load failed', verdict['note'])

    def test_no_window_is_unknown_not_unsupported(self):
        verdict = compat.judge({'scan': scan(), 'start': {'mainWindow': False, 'dialogs': [], 'status': None}})
        self.assertEqual('unknown', verdict['load'])


class TableChecks(unittest.TestCase):
    def test_merge_render_groups_consecutive_versions(self):
        with tempfile.TemporaryDirectory() as directory:
            d = Path(directory)
            results = d / 'results'
            results.mkdir()
            for version, s, st in [('4.56.1.0', scan(), started()), ('4.50.0.0', scan('9.0.0'), None),
                                   ('4.49.0.0', scan('9.0.0'), None), ('4.48.0.0', scan('8.0.0'), None)]:
                (results / f'{version}.scan.json').write_text(json.dumps(s), encoding='utf-8')
                if st:
                    (results / f'{version}.start.json').write_text(json.dumps(st), encoding='utf-8')
            data = d / 'data.json'
            compat.merge(str(results), str(data), '0.2.0', 'abcdef0', 'https://run', '2026-10-05')
            readme = d / 'README.md'
            readme.write_text(f'# x\n\n{compat.BEGIN}\nold\n{compat.END}\n\n## next\n', encoding='utf-8')
            compat.render(str(data), str(readme), str(d / 'doc.md'))
            text = readme.read_text(encoding='utf-8')
            self.assertIn('| 4.56.1.0 | ○ | ○ | ○ |', text)
            self.assertIn('| 4.49.0.0 〜 4.50.0.0（2 版） | × | — | — |', text)
            self.assertIn('| 4.48.0.0 | × |', text)
            self.assertNotIn('old', text)
            self.assertTrue(text.endswith('## next\n'))
            self.assertIn('| 4.49.0.0 | × |', (d / 'doc.md').read_text(encoding='utf-8'))
            # A re-check without the Windows steps keeps the start of the same plugin version.
            (results / '4.56.1.0.start.json').unlink()
            compat.merge(str(results), str(data), '0.2.0', 'abcdef1', 'https://run2', '2026-10-06')
            self.assertEqual('ok', json.loads(data.read_text(encoding='utf-8'))['versions']['4.56.1.0']['verdict']['load'])
            # A build that is not the release of its version says so.
            compat.merge(str(results), str(data), '0.2.0+1720188', '1720188', 'https://run3', '2026-10-07')
            compat.render(str(data), str(readme), str(d / 'doc.md'))
            self.assertIn('プラグイン 0.2.0 の後の開発版 1720188 で', readme.read_text(encoding='utf-8'))
            self.assertIn('2026-10-07、0.2.0 の後の開発版 1720188、', (d / 'doc.md').read_text(encoding='utf-8'))

    def test_plan_selects_new_and_recheck_after_plugin_change(self):
        with tempfile.TemporaryDirectory() as directory:
            d = Path(directory)
            data = d / 'data.json'
            data.write_text(json.dumps({'versions': {'4.56.1.0': {'plugin': '0.2.0'}, '4.56.0.1': {'plugin': '0.1.0'}}}), encoding='utf-8')
            server = d / 'server.txt'
            server.write_text('4.57.0.0\n4.56.1.0\n4.56.0.1\n', encoding='utf-8')
            import io, contextlib
            def plan(mode):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    compat.plan(str(data), str(server), '0.2.0', mode)
                return out.getvalue().split()
            self.assertEqual(['4.57.0.0'], plan(['new']))
            self.assertEqual(['4.57.0.0', '4.56.0.1'], plan(['stale']))
            self.assertEqual(['4.57.0.0', '4.56.1.0', '4.56.0.1'], plan(['all']))
            self.assertEqual(['4.56.1.0'], plan(['4.56.1.0', 'x']))


class ReadmeMarkerChecks(unittest.TestCase):
    def test_readme_has_the_generated_block(self):
        readme = (ROOT / 'README.md').read_text(encoding='utf-8')
        self.assertEqual(1, readme.count(compat.BEGIN))
        self.assertEqual(1, readme.count(compat.END))
        self.assertLess(readme.index(compat.BEGIN), readme.index(compat.END))


if __name__ == '__main__':
    unittest.main()
