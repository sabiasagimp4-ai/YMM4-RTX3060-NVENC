# 実機検証の手順（ChatGPTのWindows CIと、RTX 3060のPC）

NVENCと実GPUでの確認は、どのCIでもできない。開発側のCIはWARP（CPU実装）でNVENCがなく、ChatGPTのWindows CIにもRTX 3060はない。そのため、次の2段階で行う。

| 段階 | 実行する場所 | 内容 |
| --- | --- | --- |
| A | ChatGPTのWindows CI | `tools/rtx-check.ps1` を `-NoNvenc` で実行し、Windows上でスクリプト自体が動くことを確かめる。NVENCを使わない試験の結果も得る |
| B | 利用者のRTX 3060のPC | 同じスクリプトを1コマンドで実行し、できた `summary.md` をClaude Codeへ貼る |

Aでスクリプトの不具合が見つかったら、それを直してからBを行う。スクリプトはWindowsで一度も実行していない。Linux上では、スタブ（偽のdotnet・MSBuild・NativeSmoke）と実際のffmpeg・Pythonを使い、全手順・VUI実験の復元・報告の生成までを通した。

---

## A. ChatGPTへの依頼文

この節の「依頼文」以下をChatGPTへ渡す。

### 依頼文

あなたは、YMM4（ゆっくりMovieMaker4 Lite）用プラグイン「YMM4-RTX3060-NVENC」の検証の担当です。あなたのWindows CIで、検査スクリプト `tools/rtx-check.ps1` を実行してください。結果は自分で判定し、最後の「報告の形式」で返してください。

#### 背景

- このスクリプトは、利用者がRTX 3060のPCで1コマンドで実行するためのものです。ビルド、実NVENCでのスモーク試験、実ホストDLLでのキャッシュ試験、プレビュー性能、NVENCの速度、色空間の判定などを行い、`summary.md` を作ります。
- スクリプトはまだWindowsで実行したことがありません。あなたのCIにNVENCはないので、`-NoNvenc` で実行します。このモードでは、エンコードする手順（5つ）をSKIPにし、それ以外を実際に実行します。
- 目的は2つです。
  1. 利用者のPCで動かす前に、スクリプトの不具合（PowerShell 5.1との非互換、パスの扱い、終了コードの扱いなど）を見つけること。
  2. NVENCを使わない試験（ビルド、NativeChecks、ManagedSmokeのGPUなしの部分、StoreChecks、ReadinessChecks、FileLeaseChecks、CacheChecks、HostCacheProbeの `--gpu`／`--integration`／`--preview-performance`）の結果を得ること。

#### 守ること

1. リポジトリへpush・PR・issue・コメントをしない。スクリプトや製品コードを直さない。直すべき点は、差分の案として報告に書く。
2. 利用者が普段使うYMM4のフォルダーを使わない。必ず専用のコピーを使う。
3. 結果を推測で書かない。根拠は実際の出力にする。
4. 再実行は1回まで。対象は、ダウンロードの失敗やランナーの切断など、試験本体が始まる前の失敗だけとする。

#### 対象

- リポジトリ：`https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC`
- ブランチ：`claude/ymm4-nvenc-perf-analysis-5h1aik`
- ホスト：YMM4 Lite 4.56.1.0

#### 準備

必要なもの：

- .NET SDK 10
- Visual Studio 2022またはBuild Tools（「C++によるデスクトップ開発」：MSVC v143とWindows SDK）
- git
- ffmpegとffprobe：あれば `--video` 用の動画を作る
- Python 3：`-NoNvenc` では使わない

YMM4 Lite 4.56.1.0は、次のどれかで用意する。

- Git Bash（curlとjqが必要）で `bash tools/ci/fetch-ymm4.sh 4.56.1.0 C:/work/ymm4-host --top` を実行する。
- `gh release download 0.1 --repo sabiasagimp4-ai/YMM4-dlls --pattern '*.zip'` で取得して展開する。

#### A1 実行（Windows PowerShell 5.1）

利用者のPCでは `powershell.exe`（5.1）で動かす見込みのため、まず5.1で実行する。

```powershell
cd C:\work\YMM4-RTX3060-NVENC
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\rtx-check.ps1 -Ymm4DirPath C:\work\ymm4-host -NoNvenc -PreviewRuns 1 *>&1 | Tee-Object rtx-check-ps51.txt
```

#### A2 実行（PowerShell 7）

`pwsh` がある場合は、もう一度実行する。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\rtx-check.ps1 -Ymm4DirPath C:\work\ymm4-host -NoNvenc -PreviewRuns 1 *>&1 | Tee-Object rtx-check-ps7.txt
```

#### A3 確かめること

スクリプト自体について：

- 最後に `Results:`、`Summary:`、`Logs:` の3行が出て、`dist\rtx-check\<時刻>\summary.md` とzipができたか。
- 全手順に結果（PASS／FAIL／SKIP／INFO）が付いたか。途中でスクリプトが止まっていないか。
- コンソールに、スクリプト自身のPowerShellエラー（赤字）が出ていないか。出ていれば、行番号と文を記録する。
- SKIPが、`-NoNvenc` による5つ（audio-first、native-smoke、benchmark、color、vui-experiment）と、OneDriveがない場合のonedriveだけか。
- `leftovers` がPASSで、`git status --short` に追跡ファイルの変更がないか。
- `summary.md` で、パスが `<repo>`・`<ymm4>`・`<user>` に置き換わっているか。

試験の結果について：

- FAILのそれぞれを、次のどれかに分類する。根拠も書く。
  - 製品の不具合
  - スクリプトの不具合
  - 環境の問題（ツールがない、権限がないなど）
- `preview-1` の `frames pixel-exact` 行のアダプター名を書く。`Microsoft Basic Render Driver` 以外（Intel・AMDなどの実GPU）なら、その結果は実GPUでの初めての画素一致の結果になる。

スクリプトの不具合を見つけたら、最小の修正案を差分の形で書く。

#### A4 実GUIでの試験（任意。対話デスクトップがある場合だけ）

`query session` で対話セッションがない場合はSKIPとする。YMM4は専用コピーを使う。この試験はそのフォルダーへ設定とプラグインを書き込み、最後にYMM4を強制終了する。

```powershell
dotnet build NVEncVideoWriterPlugin\NVEncVideoWriterPlugin.csproj -c Release "-p:YMM4DirPath=C:\work\ymm4-host\" --nologo -v q
dotnet run --project tests\GuiSmoke\GuiSmoke.csproj -c Release "-p:YMM4DirPath=C:\work\ymm4-host\" --no-launch-profile -- C:\work\ymm4-host\ "$env:TEMP\gui-smoke.ymmp"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\ci\gui-smoke.ps1 -HostDir C:\work\ymm4-host\ -Project "$env:TEMP\gui-smoke.ymmp" -PluginDir 'NVEncVideoWriterPlugin\bin\Release\net10.0-windows10.0.19041.0' *>&1 | Tee-Object gui-smoke.txt
```

スクリーンショットは、ログの `=====SHOT` と `=====END` の間にbase64のJPEGで出る。次を報告する。

- YMM4が起動し、「描画キャッシュ」ツールが開いたか
- 保存済みの帯（緑＝RAM、青＝ディスク）が伸びたか
- 例外のダイアログが出ていないか

#### 報告の形式

````markdown
# rtx-check.ps1 -NoNvenc の結果（ChatGPTのWindows CI）

## 環境
- commit:
- OS / CPU / RAM / GPU（アダプター名）:
- PowerShell 5.1 / 7 のバージョン:
- .NET / MSBuild / ffmpeg / Python:

## スクリプト自体
| 確認 | PowerShell 5.1 | PowerShell 7 |
| --- | --- | --- |
| 最後まで動いた | | |
| summary.md と zip | | |
| スクリプト自身のエラー | | |
| SKIPが想定どおり | | |
| leftovers / git status | | |
| パスの置き換え | | |

## 試験の結果
（summary.md の Results の表を貼る。FAILには分類と根拠を付ける）

## FAILの詳細
（summary.md の Failures の節）

## スクリプトの修正案
（差分。なければ「なし」）

## 実GUI（任意）

## 気づいたこと
````

---

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
