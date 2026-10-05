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
