# RTX 3060実機検証の依頼文（ChatGPT用）

RTX 3060を積んだWindows CIを持つChatGPTへ、この文書の「依頼文」以下をそのまま渡す。ChatGPTは自分でビルド・試験・計測を行い、最後の形式で報告する。報告はClaude Codeへ貼り戻す。

---

## 依頼文

あなたは、YMM4（ゆっくりMovieMaker4 Lite）用プラグイン「YMM4-RTX3060-NVENC」の実機検証の担当です。NVIDIA GeForce RTX 3060を積んだWindows機（あなたのCI）で、ビルド・試験・計測を自分で実行してください。結果は自分で判定し、最後の「報告の形式」で返してください。利用者に手順を代行させないでください。実行できない手順は、理由を書いてSKIPにします。

### 背景

- 開発側のCIはGitHubのWindowsランナーです。Direct2DはWARP（CPU実装）で動き、NVENCはありません。実GPU・実NVENCでの結果はまだありません。今回の目的はそれを埋めることです。
- 直前に、次の問題を直しました（番号は `docs/ISSUE_REVIEW_2026-10-03.md` の番号）。CIでは確認済みですが、実機では未確認です。
  - #1：描画に関係しないYMM4の設定（タイムラインの拡大率など）を変えても、キャッシュを捨てない。
  - #2：レイヤーの名前・色・音量の変更で、関係ない区間のキャッシュを捨てない。
  - #4・#9：検証できない素材（リンク先など）は、その区間だけ通常描画し、プロジェクト全体の再検証を繰り返さない。
  - #6：取消・失敗・予定フレーム不足の出力では、途中の `.partial` ファイルを削除し、既存の完成ファイルを置き換えない。
  - #7：NVENCの出力設定（コーデック、ビットレートなど）を保存し、次回起動時に読み込む。
- まだ直していない問題で、実機の確認が必要なもの：
  - #5：NVENCの出力に色空間の情報（VUI）が入っていない。実際の変換がBT.601なのかBT.709なのかも未確認。
  - #3：OneDriveなどのクラウド同期フォルダーの素材は、キャッシュの対象外になる見込み。

### 守ること

1. リポジトリへpush・PR・issue・コメントをしない。試験を通すために製品コードを直さない。例外は §E6 の実験パッチだけで、ローカルで当てて測り、終わったら戻す（差分は報告に貼る）。
2. 利用者が普段使うYMM4のフォルダーを試験に使わない。必ず専用のコピーを使う。GUI試験（§E8）はそのフォルダーへ設定とプラグインを書き込み、最後にYMM4を強制終了するため。
3. 結果を推測で書かない。根拠は実際の出力にする。実行していない項目はSKIP、途中で止まった項目はFAILとする。
4. 一つの手順が失敗しても、それに依存しない手順は続ける。失敗した手順は、コマンド、終了コード、出力の最後の60行を残す。
5. 再実行は1回まで。対象は、ダウンロードの失敗やランナーの切断など、試験本体が始まる前の失敗だけとする。試験本体の失敗を「たまたま」と判断しない。2回目も失敗したら、その結果を報告する。
6. 長いログは添付ファイルにし、報告には判定に使った行だけを引用する。

### 対象

- リポジトリ：`https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC`
- ブランチ：`claude/ymm4-nvenc-perf-analysis-5h1aik`（この文書を含むcommit以降）
- ホスト：YMM4 Lite 4.56.1.0

### 準備

必要なもの：

- Windows 10／11 x64、NVIDIAドライバー
- .NET SDK 10
- Visual Studio 2022またはBuild Tools（「C++によるデスクトップ開発」：MSVC v143とWindows SDK）。`build.ps1` はvswhereでMSBuildを探す。
- `ffmpeg` と `ffprobe`（PATH上に必要。`build.ps1 -Smoke` が使う）
- Python 3、git

YMM4 Lite 4.56.1.0は、次のどれかで用意する。

- a. 機械にあるYMM4を作業フォルダーへコピーして使う。
- b. Git Bash（curlとjqが必要）で `bash tools/ci/fetch-ymm4.sh 4.56.1.0 C:/work/ymm4-host` を実行する。YMM4の更新サーバーから、YMM4自身の更新処理と同じ方法で取得する。
- c. `gh release download 0.1 --repo sabiasagimp4-ai/YMM4-dlls --pattern '*.zip'` で取得し、展開する（このリポジトリへの読み取り権限が要る）。

以降はPowerShellで、リポジトリの直下から実行する。試験は `dist\` へ相対パスで書き出す。

```powershell
$repo = 'C:\work\YMM4-RTX3060-NVENC'   # cloneした場所
$h    = 'C:\work\ymm4-host\'           # YMM4の専用コピー。末尾に \ を付ける
cd $repo
$logs = "$repo\dist\rtx-logs"
New-Item -ItemType Directory -Force $logs | Out-Null
(Get-Item "$h\YukkuriMovieMaker.dll").VersionInfo.FileVersion   # 4.56.1.0 であること
```

### E0 環境の記録

```powershell
git rev-parse HEAD
nvidia-smi
nvidia-smi --query-gpu=name,driver_version,memory.total,pcie.link.gen.current,pcie.link.width.current --format=csv
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion
Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
dotnet --version; (ffmpeg -version)[0]; python --version
Get-Volume | Select-Object DriveLetter, FileSystemType, DriveType, SizeRemaining
[Environment]::UserInteractive; query session
```

GPUが複数ある（内蔵GPUがある）場合は記録する。Direct3Dの既定のアダプターがRTX 3060でないと、§E3の結果が実GPUの結果にならない。

### E1 ビルドと実機スモーク（`build.ps1 -Smoke`）

```powershell
.\build.ps1 -Ymm4DirPath $h -Smoke *>&1 | Tee-Object "$logs\e1-build-smoke.log"
```

この1コマンドで、次を順に実行する。途中で失敗すると、そこで止まる。

| 段階 | 実機で確かめること | 合格を示す出力行 |
| --- | --- | --- |
| NativeChecks | ネイティブ側の不変条件 | 終了コード0 |
| ManagedSmoke | 実NVENCでのプラグインの出力。#6（取消・不足で `.partial` が残らない） | `Managed failure paths OK`、`Managed audio-first GPU output OK`、`Concurrent host audio/video and Dispose OK`、`Cancelled and incomplete GPU exports preserved existing output; complete export published OK`、`Invalid GPU input preserved existing output OK` |
| StoreChecks、ReadinessChecks、FileLeaseChecks | 保存・準備判定・素材lease | 終了コード0 |
| CacheChecks | 実ホストDLLでのキーと無効化。#1、#2、#4、#9 | `Layer settings as YMM4 saves them:`、`Per-frame keys: unrelated frames survive edits, boundaries, settings, per-frame files, scene items OK`、`Unverifiable file: only its frames render normally, idle passes them, no repeated project-wide verification OK` |
| HostCacheProbe `--gpu --video` | 実GPUでの画素一致。NVENCで作った動画を、YMM4の読込で復号する（ハードウェア復号の可能性あり）。#6の一時ファイル削除、#7の設定保存、#4・#9のidle | `NVENC export: missing frames, cancellation and failure delete the partial file OK`、`NVENC options: saved and loaded through the plugin settings and YMM4's JSON OK`、`Idle pre-render: independent scene clone, cancelled commit guard and unverifiable frames passed over OK`、終了コード0 |
| NativeSmoke | 実NVENCのH.264／HEVCとAAC。取消・失敗で異常終了しない | `Smoke OK: h264 + aac`、`Smoke OK: hevc + aac` |
| パッケージ | `.ymme` の作成 | `Package: ...\dist\YMM4-RTX3060-NVENC.ymme` と `SHA256:` |

判定：終了コード0で `Package:` 行があればPASS。止まった場合は、例外の文（例：`Managed smoke failed.`）と、その直前の出力を記録する。

止まった場合は、残りを個別に実行して、できるだけ多くの結果を得る。

```powershell
dotnet run --project tests\ManagedSmoke\ManagedSmoke.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- "$repo\dist" *>&1 | Tee-Object "$logs\e1-managed.log"
dotnet run --project tests\StoreChecksHarness\StoreChecks.csproj -c Release --no-launch-profile *>&1 | Tee-Object "$logs\e1-store.log"
dotnet run --project tests\ReadinessChecks\ReadinessChecks.csproj -c Release --no-launch-profile *>&1 | Tee-Object "$logs\e1-readiness.log"
dotnet run --project tests\FileLeaseChecks\FileLeaseChecks.csproj -c Release --no-launch-profile *>&1 | Tee-Object "$logs\e1-lease.log"
dotnet run --project tests\CacheChecks\CacheChecks.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- $h *>&1 | Tee-Object "$logs\e1-cache.log"
dotnet run --project tests\HostCacheProbe\HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- $h --gpu --video "$repo\dist\managed-audio-first.mp4" *>&1 | Tee-Object "$logs\e1-gpu.log"
```

NativeSmokeは、`build.ps1` の `if ($Smoke)` 以降と同じ手順でビルドし、`NvencNative.dll` を `tests\bin\Release\` へコピーしてから実行する。

### E2 ホスト統合（`--integration`）

`-Smoke` はこれを実行しない。出力scope、ホスト機能の判断、詳細traceの基本契約を検査する。

```powershell
dotnet run --project tests\HostCacheProbe\HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- $h --integration *>&1 | Tee-Object "$logs\e2-integration.log"
```

判定：終了コード0ならPASS。

### E3 プレビュー性能（実GPU）

3回実行し、毎回 `dist\preview-performance.json` を退避する。

```powershell
foreach ($n in 1..3) {
    dotnet run --project tests\HostCacheProbe\HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- $h --preview-performance *>&1 | Tee-Object "$logs\e3-preview-$n.log"
    Copy-Item dist\preview-performance.json "$logs\e3-preview-$n.json"
}
Select-String -Path "$logs\e3-preview-*.log" -Pattern '^PERF\|', 'pixel-exact' | ForEach-Object Line
```

確かめること：

1. `<アダプター名>: 100 frames pixel-exact` の名前が `NVIDIA GeForce RTX 3060` であること。`Microsoft Basic Render Driver` や内蔵GPUの場合は、その旨を書き、性能の比較はしない。
2. `PERF|...` 行をすべて報告に載せる。形式は `frame=p50/p95/p99/max/mean`（ms）、`stored`（保存枚数）、`busy`（読み戻し待ちで保存を見送った回数）など。
3. 次の観点で、要点を表にする。
   - cold（初回の通常描画＋保存）で、100枚中何枚を保存できたか。`busy` は0か。
   - gpu-hit／ram-hit／cold／offのp50とp95。
   - `idle-batches` のfps、p99、`renderers`。
   - 1秒を超えたフレームがあるか。

比較の基準（どちらも測定条件が違う。参考として並べるだけでよい）：

- WARP（GitHubランナー、2コア）での値：`docs/PERFORMANCE_RESULTS_2026-10-02.md` の §1と§10。files24のGPU hit p50は0.77〜0.83 ms、RAM hitは5.3〜6.2 ms、coldは11.2〜15.2 ms。idleは39.6〜100.3 fps、p99は21〜58 ms。
- 古いRTX 3060の値：`docs/preview-performance-rtx3060.json`（commit `e60c455`、当時は読み戻しが同期）。
- §10.5の見込み：「RTX 3060で全面1枚のコピーは0.1 ms未満」。軽い場面での1回描画の遅れが実GPUで消えるかを、PERF行から読み取れる範囲で書く。

### E4 NVENCのエンコード速度

```powershell
.\tests\benchmark.ps1 -Runs 3 *>&1 | Tee-Object "$logs\e4-benchmark.log"
Copy-Item dist\benchmark\results.csv "$logs\e4-benchmark.csv"
```

1080p、300フレーム、7条件。出力される表（条件ごとの中央値の秒数とファイルサイズ）を報告に載せ、fps（300 ÷ 秒数）も添える。

### E5 色空間（#5）の確認

プラグインはBGRAのテクスチャをNVENCへそのまま渡し、YCbCrへの変換はNVENCが行う（プラグインは常にNV12変換なしの経路を使う）。出力のVUIには色空間の情報がない。BT.601で変換していて情報がない場合、HD動画をBT.709とみなすプレイヤーでは色がずれる。

E1の出力（`dist\managed-audio-first.mp4`、`managed-threaded.mp4`、`smoke-h264.mp4`、`smoke-hevc.mp4`）を使う。どれも元の色が分かっている。

```powershell
python tools\color-matrix-check.py --json "$logs\e5-color.json" *>&1 | Tee-Object "$logs\e5-color.log"
ffmpeg -v info -i dist\smoke-h264.mp4 -c copy -bsf:v trace_headers -frames:v 1 -f null - 2>&1 |
    Select-String 'vui_parameters_present_flag|video_signal_type_present_flag|video_full_range_flag|colour_description_present_flag|colour_primaries|transfer_characteristics|matrix_coefficients' |
    Tee-Object "$logs\e5-vui.log"
```

`color-matrix-check.py` は、復号したY・Cb・Crの平均を、4通りの変換（BT.601／BT.709 × limited／full）で予測した値と比べる。誤差のRMSが最小のものが、NVENCの使った変換である。ローカルの試験では、正しい変換のRMSは0.4以下、次の候補は2以上だった。

報告すること：

- ファイルごとの `signaled:` 行（`color_space` などが `unknown` か）
- 4候補のRMSと `best:`
- `shown by a ... player` の4行（元の色 (127.5, 63.8, 127.5) が、各方式のプレイヤーでどう表示されるか）
- trace_headersの行（VUIがあるか）

### E6 色空間の実験パッチ（ローカルのみ）

目的：VUIでBT.709を指定したとき、NVENCの変換もBT.709に変わるのかを確かめる。修正方針がこれで決まる。変わるならVUIの指定だけで直る。変わらないなら、変換を自前で行うか、BT.601であることを正しく指定する必要がある。

1. 作業ツリーが汚れていないことを確かめる（`git status --short` が空）。
2. `NvencNative\NvencNative.cpp` の `InitializeEncoder` で、次の行の直前を探す。

   ```cpp
           if (codec == kCodecHevc)
           {
               state->config.encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;
   ```

   その直前へ、次を入れる。

   ```cpp
           // EXPERIMENT (not for commit): signal BT.709 limited range in the VUI.
           auto signalBt709 = [](NV_ENC_CONFIG_H264_VUI_PARAMETERS& vui)
           {
               vui.videoSignalTypePresentFlag = 1;
               vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
               vui.videoFullRangeFlag = 0;
               vui.colourDescriptionPresentFlag = 1;
               vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
               vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
               vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;
           };
           if (codec == kCodecHevc)
               signalBt709(state->config.encodeCodecConfig.hevcConfig.hevcVUIParameters);
           else if (codec == kCodecH264)
               signalBt709(state->config.encodeCodecConfig.h264Config.h264VUIParameters);
   ```

3. 元の出力を退避してから、再ビルドして同じ検査をする。

   ```powershell
   New-Item -ItemType Directory -Force "$logs\e6-before" | Out-Null
   Copy-Item dist\managed-*.mp4, dist\smoke-*.mp4 "$logs\e6-before\"
   .\build.ps1 -Ymm4DirPath $h -Smoke *>&1 | Tee-Object "$logs\e6-build-smoke.log"
   python tools\color-matrix-check.py --json "$logs\e6-color.json" *>&1 | Tee-Object "$logs\e6-color.log"
   ffmpeg -v info -i dist\smoke-h264.mp4 -c copy -bsf:v trace_headers -frames:v 1 -f null - 2>&1 |
       Select-String 'video_signal_type_present_flag|colour_primaries|transfer_characteristics|matrix_coefficients' |
       Tee-Object "$logs\e6-vui.log"
   ```

4. `git diff` を報告用に保存し、`git checkout -- .` で戻す。`git status --short` が空に戻ったことを確かめる。戻したあと `build.ps1 -Ymm4DirPath $h`（`-Smoke` なし）を実行し、`dist\` の `.ymme` を元のコードのものへ戻す。

報告すること：E5と比べて、`best:` が変わったか（bt601-limited → bt709-limited など）と、`signaled:` 行とVUIの値。

### E7 クラウド同期フォルダー（#3）

OneDriveがない場合（`$env:OneDrive` が空）はSKIPとする。ある場合は、同期済みのファイルを最大10個選び、属性を記録する。

```powershell
Get-ChildItem $env:OneDrive -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 10 |
    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Attributes = $_.Attributes.ToString() } } | Format-Table -AutoSize
fsutil reparsepoint query "<上で選んだファイルの1つ>"
```

確かめること：`ReparsePoint`、`Offline`、`RecallOnDataAccess`（0x400000）、`RecallOnOpen`（0x40000）などの属性があるか。「このデバイス上に常に保持する」に設定したファイルにもリパースポイントが付くか。付く場合、そのファイルを使う区間はキャッシュされない（現在の仕様）。

### E8 実GUIでの試験（対話デスクトップがある場合だけ）

`query session` で対話セッションがなく、サービスとして動いているランナーではSKIPとする。YMM4は必ず専用コピー（`$h`）を使う。

```powershell
dotnet build NVEncVideoWriterPlugin\NVEncVideoWriterPlugin.csproj -c Release "-p:YMM4DirPath=$h" --nologo -v q
dotnet run --project tests\GuiSmoke\GuiSmoke.csproj -c Release "-p:YMM4DirPath=$h" --no-launch-profile -- $h "$env:TEMP\gui-smoke.ymmp"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\ci\gui-smoke.ps1 -HostDir $h -Project "$env:TEMP\gui-smoke.ymmp" -PluginDir 'NVEncVideoWriterPlugin\bin\Release\net10.0-windows10.0.19041.0' -ArtifactDirectory "$logs\e8-gui" *>&1 |
    Tee-Object "$logs\e8-gui.log"
```

`gui-smoke.ps1` はWindows PowerShell 5.1（`powershell.exe`）で動かす。スクリーンショットはログの `=====SHOT` と `=====END` の間にbase64のJPEGで出る。見て、次を報告する。

- YMM4が起動し、プロジェクトと「描画キャッシュ」ツールが開いたか
- 停止中の先読みで、保存済みの帯（緑＝RAM、青＝ディスク）が伸びたか
- 例外のダイアログが出ていないか

### E9 後始末と残骸の確認

```powershell
Get-ChildItem $env:TEMP -Filter '.*.partial' -Force -ErrorAction SilentlyContinue | Select-Object FullName, Length, LastWriteTime
Get-ChildItem $env:TEMP -Directory -Filter 'ymm-nvenc-staging-*' -ErrorAction SilentlyContinue | Select-Object FullName
git status --short
```

- ManagedSmokeとHostCacheProbe（`--gpu`）がTEMPへ作る `.<名前>.mp4.<GUID>.partial` は、残っていてはいけない（#6）。
- `dist\smoke-*-cancel.partial` と `dist\smoke-*-failure.partial` は残ってよい。NativeSmokeがネイティブ層を直接呼ぶ試験の出力で、ネイティブ層は削除しない（削除はプラグイン側が行う）。
- `git status --short` が空であること（`dist\` は追跡対象外）。

### 報告の形式

次のMarkdownで返す。数値は実測値だけを書く。分からない項目は「不明」と書く。

````markdown
# RTX 3060実機検証の結果

## 環境
- commit:
- GPU / ドライバー / VRAM:
- 他のGPU:
- CPU / RAM / OS:
- .NET / ffmpeg / Python:
- YMM4: 4.56.1.0（取得方法：a／b／c）
- 対話デスクトップ: あり／なし

## 結果一覧
| ID | 項目 | 結果 | 根拠（出力行やファイル） |
| --- | --- | --- | --- |
| E1 | build.ps1 -Smoke | PASS/FAIL/SKIP | |
| E1-M | ManagedSmoke（#6、実NVENC） | | |
| E1-C | CacheChecks（#1 #2 #4 #9） | | |
| E1-G | HostCacheProbe --gpu --video（#6 #7 #4 #9） | | |
| E1-N | NativeSmoke h264/hevc | | |
| E2 | --integration | | |
| E3 | preview-performance ×3 | | |
| E4 | NVENC benchmark | | |
| E5 | 色空間の判定 | | |
| E6 | VUI実験 | | |
| E7 | OneDrive属性 | | |
| E8 | 実GUI | | |
| E9 | 残骸 | | |

## 失敗の詳細
（手順ごとに、コマンド、終了コード、最後の60行）

## E3 プレビュー性能
- アダプター:
| fixture | mode | 1回目 p50/p95 | 2回目 | 3回目 | stored | busy |
| --- | --- | --- | --- | --- | --- | --- |
- idle-batches: fps, p99, renderers
- 1秒を超えたフレーム:
- WARPと古いRTX 3060の値との比較（要点のみ）:
- PERF行の全文: 添付 e3-preview-*.log

## E4 エンコード速度
| 条件 | 中央値（秒） | fps | サイズ |
| --- | --- | --- | --- |

## E5／E6 色空間
| ファイル | signaled | best | RMS（best / 次点） | E6のbest | E6のsignaled |
| --- | --- | --- | --- | --- | --- |
- E5 `shown by` の4行:
- E5／E6 VUIの行:
- E6の差分（git diff）:

## E7 OneDrive
## E8 実GUI
## 気づいたこと
（期待と違った挙動、警告、遅い箇所。推測は推測と明記）

## 添付
（dist\rtx-logs の一覧）
````
