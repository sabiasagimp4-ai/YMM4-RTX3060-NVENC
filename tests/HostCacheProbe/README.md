# 実ホストのキャッシュ検証

.NET 10のWindows用probe。指定したYMM4のDLLをその場所から読み、実行中だけHarmony 2.4.2で接続する。ホストのバイナリは変更しない。基準はYMM4 Lite 4.56.1.0。

使用するホストをビルド時と実行時の両方へ指定する。

```powershell
$hostPath = 'D:\YukkuriMovieMaker_v4_Lite\'
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$hostPath" -- $hostPath --integration
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$hostPath" -- $hostPath --gpu
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release "-p:YMM4DirPath=$hostPath" --no-launch-profile -- $hostPath --preview-performance
```

| オプション | 検査するもの |
| --- | --- |
| --integration | 出力scope、ホスト機能の判断、詳細traceの基本契約 |
| --gpu | WARP／host graphicsによる画素一致、preview／export供給、選択枠、編集・素材・世代無効化、idle複製、資源所有権 |
| --video <path> | --gpuに追加して実動画デコード失敗の非保存と復旧を検査 |
| --preview-performance | OFF／cold／RAM比較、8枚反復のRAM／GPU各3pass、GPU保持・退避borrow・無効化・解放・traceを検証 |
| --trace-output <path> | ホスト試験のJSONLログを新しいファイルへ採取 |
| --unread | 読み取り済み版とのcontracts照合で有効な機能だけを検証 |

性能fixtureの結果は `dist/preview-performance.json`、RAM／GPUのtraceは同じdistへ出力する。比較のwarmupと画素検査は測定外。非待機readbackではGPU処理中に新しい保存を見送れるため、coldの保存枚数を記録し、RAM-hitの全フレーム準備は測定外で明示的に完了させる。軽いfixtureでcache OFFが速い場合もあり、測定値をGUIのFPSへ置き換えない。

mainの `cache-development` CIではportable、native、file lease、依存キー、--integration／--gpu／--preview-performanceを実行する。`YMM4-dlls` のCIテンプレートでは動画fixtureと実GUI試験も実行する。CIのWARP検証とRTX 3060上のNVENC smokeは別。GPU計測はCPU wall timeで、GPU実行時間を測っていない。

portableな検査はホストなしで実行できる。

```sh
python -m unittest discover -s tests/tools -v
dotnet run --project tests/StoreChecksHarness/StoreChecks.csproj -c Release
dotnet run --project tests/ReadinessChecks/ReadinessChecks.csproj -c Release
```

試験の条件・結果は [開発方針](../../docs/AE_CACHE_DEVELOPMENT.md)、計測の意味は [診断](../../docs/CACHE_DIAGNOSTICS.md)、ホスト更新は [契約](../../docs/HOST_CONTRACTS.md) を参照。

### PSD立ち絵（計画2c）

```powershell
dotnet run --project tests/HostCacheProbe -c Release "-p:YMM4DirPath=D:\YMM4\" -- D:\YMM4 --psd-tachie-check
dotnet run --project tests/HostCacheProbe -c Release "-p:YMM4DirPath=D:\YMM4\" -- D:\YMM4 --psd-tachie-measure
```

自作7layer PSDの画素、非表示／母音、設定通知、非通知Offset／Layersの実画素反例、共有sidecar、PSD上書き、時間切れ・部分失敗／取消、設定上限、CPU合成失敗と回復、最初の指紋より前のPSD上書き、アプリ全体のJSON設定の影響を独立STA／デバイス・各30秒以内で検査する。計測は2体・20voice・60秒15fps900frame。`SPEEDUP2C` の `cache_hits`／`ms_per_frame` と `SPEEDUP2C_PIXELS` を出す。referenceとcacheの全900frameを全byte比較し、readbackは時間に含めない。WARPの値をRTX3060の速度としない。

アニメーション立ち絵の `--animation-tachie-check` は、120 frameでキャッシュのオン・オフを切り替える `output-lifetime` も検査する。通常描画と全byte一致し、元のhost command listが更新ごとに管理リストへ蓄積しないことを確認する。


## Claude レビュー追加検査（2026-10-05）

`--animation-tachie-large` は 309 PNG のフォルダーと 900 フレーム、`--psd-tachie-large` は各 109 MiB の生成 raw layered PSD と 300 アニメーション設定、120 フレームを使い、2 回目の Update+Draw 時間・hit・全バイトの画素一致を測る。各 2 回、専用 STA / device。GPU 保持 OFF / RAM 256 MiB / WARP。画素読み出しは 2 回目の時間に含めない。生成素材のみで、実機の GUI・音声・Present を測らない。

立ち絵が表示区間外の先読みは 6 フレームの実 RAM hit と画素一致を追加。表示区間は見送りを検査する。animation はフォルダー時刻を戻した新部品を拒否し、PSD は 100 回の非通知編集を従来の fresh serializer と全値で照合、同値 JSON の再利用、A/B/A の復元を検査する。既存の遅延・失敗・上書き・古い正規化・外部 serializer defaults の検査は残す。

固定の過去 commit を取る 8 手順は workflow_dispatch の measure_baselines=true の時だけ動く。上記の検査・大きな素材の測定は常時動く。PR #14 は main 独立の出力寿命・inactive random idle の regression と各修正の個別除去による失敗を tools/ci/verify-host-output-idle.ps1 で検査する。最終 head の CI・追加計測は各 PR に記録する。

## 診断レポートの操作検査

`--diagnostic-ui-check` は STA の WPF 窓で、読み取り専用の送信本文・詳細、明示操作のみのコピー/保存/ブラウザー、保存取消・非同期保存・操作失敗、不正 URL の拒否と実プラグインの「問題を報告」ボタンを検査する。全アクションは fake で、ブラウザーも実 Issue も開かない。`--integration` は未読ホストの契約拒否が診断へ記録され、正常な接続では増えないことも検査する。portable の収集・秘匿・上限・URL 検査は `dotnet run --project tests/DiagnosticChecks -c Release`。

## PSD の同名依存 DLL の反例

`--psd-duplicate-parser` / `--psd-duplicate-source` は、real host の正常な module verdict をキャッシュした後、専用 AssemblyLoadContext に同じ DLL を二重ロードする。PSD の判定が例外なく false へ変わること、立ち絵のない図形の区間が実 RAM hit と全バイト画素一致を保つことを検査する。DLL は検査 process の終了まで載るので、各反例は別 process で実行する。通常 CI では常時実行し、素材の固定 commit の測定とは独立する。
