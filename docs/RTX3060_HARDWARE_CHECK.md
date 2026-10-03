# 実機検証の手順（GitHub ActionsとRTX 3060のPC）

検査は `tools/rtx-check.ps1` の1コマンドで行う。NVENCと実GPUを使う手順は、RTX 3060のPCでしか実行できない。GitHubのWindowsランナーにはGPUがなく、Direct2DはWARP（CPU実装）で動き、NVENCもないためである。そこで、次の2段階で行う。

| 段階 | 実行する場所 | 内容 |
| --- | --- | --- |
| A | GitHub ActionsのWindowsランナー | `-NoNvenc` で実行する。Windows PowerShell 5.1と7の両方で、スクリプト自体が動くことを確かめる。NVENCを使わない試験の結果も得る |
| B | 利用者のRTX 3060のPC | NVENCの手順を含めて実行する。できた `summary.md` をClaude Codeへ貼る |

## A. GitHub Actionsで実行する

`cache-development` ワークフローを手動で実行し、入力 `rtx_check` をオンにする。

- GitHubの画面では：Actions → cache-development → Run workflow → ブランチを選び、「Also run tools/rtx-check.ps1 -NoNvenc ...」にチェックを入れる。
- 通常の試験に加え、`rtx-check (powershell)` と `rtx-check (pwsh)` の2つのジョブが動く。
  - 各ジョブは、ログの最後に `summary.md` を表示する。
  - `dist/rtx-check` をartifact `rtx-check-powershell`／`rtx-check-pwsh` として残す。
- このモードでは、エンコードする5つの手順（audio-first、native-smoke、benchmark、color、vui-experiment）をSKIPにする。OneDriveのないランナーではonedriveもSKIPになる。

### 実行結果（2026-10-03）

run `37106722408`（commit `8ead5cf`、windows-2022、2コア・8 GB）での結果：

| 項目 | PowerShell 5.1.20348 | PowerShell 7.6.6 |
| --- | --- | --- |
| 結果 | PASS 11、SKIP 6 | PASS 11、SKIP 6 |
| スクリプト自身のエラー | なし | なし |
| 所要時間 | 約6分 | 約5.7分 |
| leftovers（残骸・追跡ファイルの変更） | なし | なし |

- 期待した出力行はすべて出た。
  - CacheChecksの3行（#1 #2 #4 #9）
  - HostCacheProbe `--gpu --video` の3行（#6 #7 #4 #9）
  - ManagedSmokeのGPUを使わない3行
- アダプターは `Microsoft Basic Render Driver`（WARP）で、100フレームの画素が一致した。
- WARPでの計測値（files24、PowerShell 5.1／7）：
  - GPU hitのp50：0.57〜0.77 ms
  - RAM hitのp50：4.5〜5.1 ms
  - 保存の見送り（`busy`）：全パスで0
  - idle：68.8〜75.7 fps、描画器の作成は1回

NVENCを使う手順も、一度だけWindowsで動かした（run `37107197357`、PowerShell 5.1）。ランナーにNVENCはないので、偽の `nvidia-smi` を置いて実行した。

- NVENCが要る手順は、想定どおり `nvEncodeAPI64.dll not found` で失敗した。
- VUI実験のパッチはMSVCでコンパイルできた。
- ソースは元に戻り、作り直し（restore-build）と残骸の確認（leftovers）はPASSした。
- 報告には5つの失敗がすべて載った。

この確認のためのジョブは、確認後に削除した。

## B. RTX 3060のPCで行うこと（利用者）

### 準備（初回だけ）

次を入れる。wingetでの例を示す。

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
winget install Gyan.FFmpeg
winget install Python.Python.3.12
winget install Git.Git
```

1. 新しいPowerShellを開き、`dotnet --version`、`ffmpeg -version`、`python --version` が動くことを確かめる。
2. リポジトリを取得する。

   ```powershell
   git clone -b claude/ymm4-nvenc-perf-analysis-5h1aik https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC C:\work\YMM4-RTX3060-NVENC
   ```

3. 普段使うYMM4のフォルダーを、丸ごと別の場所（例：`C:\work\ymm4-host`）へコピーする。試験はこのコピーのDLLを読む。

### 実行

YMM4を閉じてから実行する。数十分かかる。プレビュー性能やNVENCの速度を測るので、その間はPCで重い作業をしない。

```powershell
cd C:\work\YMM4-RTX3060-NVENC
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rtx-check.ps1 -Ymm4DirPath C:\work\ymm4-host
```

### 終わったら

最後に表示される `Summary:` のファイル（`dist\rtx-check\<時刻>\summary.md`）の中身を、Claude Codeへ貼る。

- 全ログは、同じ名前のzipにまとまっている。必要なときだけ頼む。
- `summary.md` では、パスが `<repo>`・`<ymm4>`・`<user>` に置き換わっている。zipの中のログには、元のパスが残る。

### スクリプトが行うこと

| 手順 | 内容 | 関係する問題 |
| --- | --- | --- |
| build | `build.ps1`（ネイティブ、プラグイン、ホストでの読み込み確認、`.ymme` の作成） | |
| native-checks | ネイティブ側の不変条件 | |
| managed-smoke | 実NVENCでのプラグインの出力。取消・不足・失敗で `.partial` が残らないこと | #6 |
| audio-first | 音声を先に書いたMP4の、ストリーム・復号・音声サンプル | |
| store／readiness／file-lease-checks | 保存、準備の判定、素材のlease | |
| cache-checks | 実ホストDLLでのキーと無効化 | #1 #2 #4 #9 |
| host-gpu | 実GPUでの画素一致。NVENCで作った動画の読込。一時ファイルの削除と設定の保存 | #4 #6 #7 #9 |
| host-integration | 出力scope、ホスト機能の判断、traceの契約 | |
| native-smoke | NVENCのH.264／HEVCとAAC、取消・失敗 | |
| preview-1〜3 | 実GPUでのプレビュー性能（3回） | |
| benchmark | NVENCの速度（1080p、300フレーム、7条件×3回） | |
| color | NVENCが使った色変換（BT.601／BT.709、limited／full）と、VUIの有無 | #5 |
| onedrive | OneDriveのファイルの属性（ファイル名は記録しない） | #3 |
| vui-experiment | VUIでBT.709を指定すると、NVENCの変換も変わるか。`NvencNative.cpp` を一時的に書き換え、終わったら元のバイト列に戻す | #5 |
| restore-build | 元のソースで作り直す | |
| leftovers | 一時ファイルの残骸がないこと、追跡ファイルが変わっていないこと | #6 |

一つの手順が失敗しても、残りは続けて実行する。スクリプトはYMM4のフォルダーへプラグインを入れず、YMM4も起動しない（試験はそのDLLを読み込んで使う）。指定したフォルダーのYMM4が起動中なら、実行を断る。pushはしない。
