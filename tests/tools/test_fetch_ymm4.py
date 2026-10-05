import base64
import functools
import hashlib
import http.server
import json
import os
import shutil
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'tools/ci/fetch-ymm4.sh'


def publish(root, version, files):
    """A version folder as YMM4's update server lays it out: the files and YukkuriMovieMaker.json (with a BOM)."""
    folder = Path(root, 'Application Files', 'YukkuriMovieMaker_' + version.replace('.', '_'))
    entries = []
    for name, content in files.items():
        path = folder.joinpath(*name.split('\\'))
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        entries.append({'File': name, 'Hash': base64.b64encode(hashlib.sha256(content).digest()).decode(), 'Size': len(content)})
    (folder / 'YukkuriMovieMaker.json').write_text(json.dumps({'HashAlgorithm': 'SHA256', 'Files': entries}, ensure_ascii=False), encoding='utf-8-sig')


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


@unittest.skipUnless(all(shutil.which(tool) for tool in ('bash', 'curl', 'jq', 'openssl')), 'needs bash, curl, jq and openssl')
class FetchChecks(unittest.TestCase):
    """tools/ci/fetch-ymm4.sh against a local copy of the update server's layout."""

    @classmethod
    def setUpClass(cls):
        cls.server_root = tempfile.mkdtemp()
        publish(cls.server_root, '1.0.0.0', {
            'YukkuriMovieMaker.dll': b'v1', 'old.dll': b'old', '_起動しない場合.txt': '読む'.encode(),
            'Resources\\bin\\YukkuriMovieMaker.Win32Service.exe': b'service 1',
            'Resources\\bin\\x64\\ffmpeg\\avcodec-62.dll': b'x' * 1000, 'Resources\\SoundFonts\\a.sf2': b'sf',
            'Resources\\Transition\\円.png': b'png', 'runtimes\\cuda\\win-x64\\a.dll': b'cuda',
            'runtimes\\win-x64\\native\\WebView2Loader.dll': b'webview',
        })
        publish(cls.server_root, '1.1.0.0', {
            'YukkuriMovieMaker.dll': b'v2', '_起動しない場合.txt': '読む'.encode(),
            'Resources\\bin\\YukkuriMovieMaker.Win32Service.exe': b'service 2', 'Resources\\Transition\\円.png': b'png',
        })
        Path(cls.server_root, 'versionlist2.php').write_text('1.0.0.0\r\n1.1.0.0\r\n')
        handler = functools.partial(QuietHandler, directory=cls.server_root)
        cls.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), handler)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        shutil.rmtree(cls.server_root, ignore_errors=True)

    def fetch(self, *args, prune=False):
        env = dict(os.environ, YMM4_UPDATE_BASE=f'http://127.0.0.1:{self.server.server_port}',
                   NO_PROXY='127.0.0.1', no_proxy='127.0.0.1')
        if prune:
            env['YMM4_FETCH_PRUNE'] = '1'
        return subprocess.run(['bash', str(SCRIPT), *args], env=env, capture_output=True, text=True, timeout=60)

    def files(self, folder):
        return sorted(str(p.relative_to(folder)).replace(os.sep, '/') for p in Path(folder).rglob('*') if p.is_file())

    def test_list_is_newest_first(self):
        self.assertEqual(['1.1.0.0', '1.0.0.0'], self.fetch('list').stdout.split())

    def test_app_takes_subfolders_and_japanese_names_but_not_the_large_optional_parts(self):
        with tempfile.TemporaryDirectory() as app:
            result = self.fetch('1.0.0.0', app, '--app')
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(['Resources/Transition/円.png', 'Resources/bin/YukkuriMovieMaker.Win32Service.exe',
                              'YukkuriMovieMaker.dll', '_起動しない場合.txt', 'old.dll',
                              'runtimes/win-x64/native/WebView2Loader.dll'], self.files(app))

    def test_next_version_in_the_same_folder_updates_and_prunes_but_keeps_user(self):
        with tempfile.TemporaryDirectory() as app:
            Path(app, 'user').mkdir()
            Path(app, 'user', 'setting.json').write_text('kept')
            self.assertEqual(0, self.fetch('1.0.0.0', app, '--app', prune=True).returncode)
            result = self.fetch('1.1.0.0', app, '--app', prune=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(['Resources/Transition/円.png', 'Resources/bin/YukkuriMovieMaker.Win32Service.exe',
                              'YukkuriMovieMaker.dll', '_起動しない場合.txt', 'user/setting.json'], self.files(app))
            self.assertEqual(b'v2', Path(app, 'YukkuriMovieMaker.dll').read_bytes())
            self.assertEqual(b'service 2', Path(app, 'Resources', 'bin', 'YukkuriMovieMaker.Win32Service.exe').read_bytes())

    def test_top_takes_only_the_application_folder_itself(self):
        with tempfile.TemporaryDirectory() as app:
            self.assertEqual(0, self.fetch('1.0.0.0', app, '--top').returncode)
            self.assertEqual(['YukkuriMovieMaker.dll', '_起動しない場合.txt', 'old.dll'], self.files(app))

    def test_a_changed_file_on_the_server_is_rejected(self):
        with tempfile.TemporaryDirectory() as app:
            target = Path(self.server_root, 'Application Files', 'YukkuriMovieMaker_1_1_0_0', 'YukkuriMovieMaker.dll')
            target.write_bytes(b'v3')
            try:
                result = self.fetch('1.1.0.0', app, '--match', r'^YukkuriMovieMaker\.dll$')
                self.assertNotEqual(0, result.returncode)
                self.assertIn('hash mismatch: YukkuriMovieMaker.dll', result.stderr)
            finally:
                target.write_bytes(b'v2')


if __name__ == '__main__':
    unittest.main()
