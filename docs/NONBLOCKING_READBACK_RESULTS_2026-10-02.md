# ライブプレビューの非待機readback

従来の1フレーム遅延readbackは、D2D `Map(Read)`でGPU完了を待つ場合がありました。30秒・421アイテムの同期版ログではMapWait p50 420.831 ms、p95 458.131 ms（45回）です。この待機をライブプレビューの描画タスクから除く変更を実装しました。

## 実装

- 実際のD2D targetのDXGI surfaceからD3D11 textureとdeviceを取得。別device・別描画スレッドへ流用しません。
- EndDrawでD2D描画を提出してから同じdeviceのstaging textureへCopyResourceし、ImmediateContext.Flushでコピーを提出します。
- ライブプレビューは`Map(Read, DoNotWait)`で確認。GPU処理中なら配列を確保せず、次のsource更新／player loopへ保留します。同期Mapへのフォールバックはありません。
- sourceごとの保留は1枚。処理中の新しい保存は省略して通常のホスト描画を続けます。CPU memcpyや新しい描画を無料にするものではありません。
- 完了してからrow pitchを使って画素をコピーし、Unmap。保存直前に依存・世代・設定を再確認します。消去中にmapped textureを破棄しないよう、回収中の所有権を保留stateから切り離します。
- 明示的なCapturePreview、idle先読み、テストの完了helperは同期完了を使います。GPU保持は従来どおり復元画像を対象とします。
- `readback-poll` ready／gpu-busy、`cache-admission` readback-busyを記録し、GPU処理中と保存見送りを区別します。

Microsoftの契約: [DO_NOT_WAIT](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_map_flag)、[EndDraw](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-enddraw)。CPU wall timeをGPU timestampと扱いません。

## キャンセル処理と検証

最初の実GUI実行では「The operation was canceled」例外ダイアログがあり、再訪・編集のgateが失敗しました。この実行を成功扱いしません。背景の素材検証Taskに、編集／設定変更／disposeのキャンセルが未観測のfaultとして残り得る経路を見つけ、検証不可の結果へ戻すよう修正しました。未完成の素材検証結果はキーとして採用しません。GUI harnessは例外ダイアログの詳細をclipboardから保存して停止し、ホストログもartifactへ保存します。

Windows CIで次を確認しました。

- キャンセル済みtokenでの素材検証とworker失敗は、Taskのfaultを残さず検証不可の結果を返す。
- 奇数寸法、透明度、ズーム・パンを含む非待機readback画素が明示的captureと一致する。
- 再Mapでき、二重DisposeでもGPU予約は解放される。
- 100フレームのWARP画素比較、GPU保持・borrow・編集・消去・source破棄の回帰テストが通過する。
- cold測定では保存枚数を記録し、100% RAM-hitの準備は測定外で明示的に完了させる。GPU処理中の保存見送りをテストで禁止しません。

WARPの軽いFull-HD fixtureではcold 100枚を保存し、MapWaitは198回、p50 0.0324 ms、p95 0.0904 msでした。最後の明示的flushは測定ループ外です。これはGUI・動画デコード・Present・音声を含むFPSではありません。詳細は [warp-performance.json](traces/2026-10-02-nonblocking/warp-performance.json)。

ソースruntime: `6b39416817c8bf1f261975f978ba9b83c7aad2c6`。
CI: https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/actions/runs/37013484288
失敗した先行GUI: https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/37012093721

## 30秒・421アイテムの実GUI再試験

RAM上限64 MiB、idle先読みOFF。修正版のGUI gateは成功し、例外ダイアログは検出されませんでした。

| 項目 | 同期版（前回） | 非待機版（今回） |
| --- | ---: | ---: |
| MapWait回数 | 45 | 183（完了48・処理中135） |
| MapWait p50 / p95（ms） | 420.831 / 458.131 | 0.0099 / 13.7344 |
| MapWait平均（ms） | 345.654 | 2.489 |
| 保存の描画スレッド負担（ms/枚） | 457.1（45枚） | 112.8（48枚） |
| Playing更新 無効 / 初回 / 2回目 | 22 / 17 / 16 | 26 / 17 / 19 |
| 詳細ログ | 11,328・欠落0・未完了0 | 14,127・欠落0・未完了0 |

同じfixture・同じRAM上限の別CI実行です。MapWaitは完了／処理中probeを含み、GPU実行時間ではありません。非待機APIでもドライバー処理・OS schedulingがあり、最大のMap呼出しは120.154 msでした。コピー開始のp95も396.969 msあり、D2D提出を含む費用は残ります。保存負担の値はツールのpreview-store ticksを保存枚数で割った値です。普遍的な速度向上率やRTX3060での効果を主張しません。

Playingの初回Update p50は前回1404.602 ms、今回1591.788 msで、通常再生全体の改善を示していません。動画更新が重く、2回目にも初回に未描画だった時刻を新しく描きます。900フレームすべてがキャッシュ済みの再生ではありません。更新回数をFPSとも扱いません。

4つの既描画時刻（4・36・84・188フレーム）への初回再訪はディスク4 hit、2回目は同じ4時刻のGPU4 hit。再訪2のGPU Update（frame0を含む5回）はp50 5.591 ms、p95 5.702 msでした。削除・Undo・Redo・再Undoは保存projectで421→420→421→420→421を確認し、キャッシュ消去も成功しました。

- GUI: https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/37013534089
- mirror: `16f0b529b72008939577340f64ead5138106ed32`
- [生ログgzip](traces/2026-10-02-nonblocking/ram-64.jsonl.gz)・[集計](traces/2026-10-02-nonblocking/ram-64-summary.json)・[SHA256とstage統計](traces/2026-10-02-nonblocking/manifest.json)

## 残る課題

非待機Mapは同期readbackのGPU完了待ちを除く変更です。FFmpegの動画更新、D2Dの描画提出、CPUコピー、依存検証、ディスク復元の費用は残ります。全900フレームの事前生成、音声時計を含むCache Before Playback、ホストのitem／effect段の共有、MFRはこの変更に含みません。RTX3060実機FPS、GPU timestamp、音声同期を測ったものでもありません。
