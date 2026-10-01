# プレビューキャッシュ改善の指針・チェックポイント

2026-10-01。対象は **YMM4 4.56.1.0のみ**。
branch: `codex/preview-scheduler`、base: `56e9441`。
ユーザーの即時コミット指示により、この段階で実装を停止して保存する。

## 保存した変更

- `PreviewPerformance` に1024件のrolling p50/p95、全サンプルのcount/meanを集約。
- `TimelineSource.Update` のOFF／bypassも計測。開始位置を前フレームのdeferred readbackより前へ移し、finalizerのcleanupまでを `TotalUpdate` に含める。
- キー生成、lookup（照合・upload・read-aheadを含む）、host render、GPU copy submission、配列確保、Map待ち、CPUコピー、RAM commit、disk enqueueを分離。
- 従来のpath別時間も保存を含むUpdate全体へ変更。host render単体は `HostRender` を参照する。
- 実playerのDraw単体を `PreviewDraw` に記録。同source・同threadの直前Updateを一度だけ対応させ、Update+DrawのCPU時間を `TotalPreview` に記録する。
- `TotalPreview` にPresent、音声待ち、フレーム間の間隔、scheduler待ちは含めない。GPU copyはGPU実行時間でなくCPU側のsubmission時間。
- 100フレームのOFF／cold-store／RAM-hit比較を `HostCacheProbe --preview-performance` に追加。画素検証は測定後の別パス。

## 取得済みデータと限界

`preview-performance-rtx3060.json` はこのチェックポイントのローカル測定結果。
実DLLのTimelineSource、RTX 3060、1920×1080、30fpsプロジェクトの100フレーム。
移動する半透明ShapeItemとArial TextItemを使用し、diskは無効。
OFF／ONとも計測フックは有効。フレームレートでのpacingは行っていない。
実GUI/audio/swap-chainではなく、source-onlyのDrawをテストが代行する。
100フレームの画素一致、cold-storeの100保存、RAM-hitの100ヒットはJSON作成前のassertで確認。
最終deferred flushは別計測。JSONはcleanup終了前に出力するため、これだけでは終了時GPU解放の証拠にしない。
計測接続時のReleaseビルドは警告0・エラー0。ベンチ追加後も実行してJSONを取得した。
既存全回帰試験、実YMM GUI、NVENC再検証、package/releaseはこのチェックポイントでは実施していない。

## 次の実装順序

1. 計測の回帰試験と同fixtureの再測定を行う。既存画素一致・decoder readiness・編集/素材更新・世代無効化・GPU所有権の試験を通す。
2. 再生中のCPU readbackを外す。既存のviewport/key・不透明黒背景の画素契約を維持してGPU L0を追加し、停止時にRAM/diskへ降格する。
3. 同じD2D/D3D deviceとcontextでの実経路を確認し、GPU完了を確認できた枠だけMapするreadbackリングを導入する。
4. 音声時計も停止できるキャッシュ優先schedulerを実装する。連続区間をhigh-water 30／low-water 5を目安に蓄積し、リアルタイム優先も選べるようにする。
5. 停止中の先読みをCTI前方→後方→遠方へ伸ばす。固定10秒horizonと固定Sleep(8)に依存せず、操作・編集・予算・タイムライン境界で中断する。
6. `FrameCacheStore` の動的RAM budgetを実装する。ロック内で予算更新とLRU退避を行い、圧力時は縮小、安定した回復時だけ段階的に増やす。手動上限を越えない。

## 守る契約・未解決点

- D2D `Map(Read)` にDoNotWaitはない。1フレーム遅延やリングだけで非ブロックと扱わない。D3D11 event queryは候補だが、D2D flushとqueryの順序・device一致・staging経路の実証が先。
- 同じD2D contextを別workerで並行使用しない。capture lease、generation、viewport signature、decoder readinessを維持し、stale frameを保存・表示しない。
- `TimelineAudioPlayer.Position` が再生時計。映像だけ止めると音声が先行する。`StopAsync()` はゼロへseekし、`EndAudioTask()` はリピートON時にStartPositionへ戻すため、そのままbuffer待機に使わない。
- UI/render workerでasync toggleを同期Waitしない。待機中の停止・seek・編集・repeat・末尾・disposeを含めた取消契約を先に確認する。
- RAM退避は辞書参照を外す。借用済みReadOnlyMemoryを変更しない。RamBytesはwrite queueや借用配列を含むプロセス全体の使用量ではない。
- disk readの配列確保前に最新RAM予算を確認する。disk indexのvalidityとRAM promotion条件を分離し、RAM縮小でdisk recordを削除しない。
- メモリpolicyはOS空き物理RAM・GC圧力・process private bytesを背景samplingする。取得失敗で増やさず、queue/最大1frame分の余裕を確保し、縮小/回復hysteresisを合成snapshotで検証する。

## 再開コマンド

```powershell
dotnet run --project tests/StoreChecksHarness/StoreChecks.csproj -c Release
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release -- 'D:\YukkuriMovieMaker_v4_Lite' --preview-performance
dotnet run --project tests/HostCacheProbe/HostCacheProbe.csproj -c Release -- 'D:\YukkuriMovieMaker_v4_Lite' --gpu
```

元worktree `YMM_keiryouka` の途中変更は保持しており、このbranchには混ぜていない。
旧ログ・設定・リリース作業は停止中。サブエージェントは停止・引継ぎ済み。
このコミットは計測と指針のWIPであり、GPU L0・readbackリング・scheduler・動的RAM予算の完成やリリースを意味しない。
