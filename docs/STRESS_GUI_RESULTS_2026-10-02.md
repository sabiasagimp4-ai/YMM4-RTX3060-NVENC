# 30秒の動画・テキスト負荷試験

1920×1080・30fps・900フレーム。動画120アイテム（12本同時表示）、文字300アイテム（10行同時表示・4行に標準ぼかし）、音声1アイテム。素材はFFmpegの試験信号です。

実際のYMM4 Lite 4.56.1.0をWindows CIで起動し、無効／初回／再再生、既描画時刻への再訪、シーク、削除、Undo、Redo、キャッシュ消去を実行しました。RTX3060実機の性能測定ではありません。

## 確認した結果

| RAM上限 | Playing更新 無効／初回／2回目 | 再生時刻の最大（秒） | 完全なログ | 編集結果 |
|---|---:|---:|---|---|
| 256 MiB | 20／15／14 | 28.43／28.40／28.87 | 10,495件・欠落0・未完了0 | 421→420→421→420→421 |
| 64 MiB | 22／17／16 | 27.77／29.07／29.87 | 11,328件・欠落0・未完了0 | 421→420→421→420→421 |

更新回数はFPSではありません。ソース時刻は約30秒まで進んでいますが、重いCI環境では大幅にフレームが落ちています。2回目の再生では初回に未描画だった時刻が要求され、ほぼ新規描画になりました。全900フレームがwarmになった試験ではありません。

256MiB条件でRAM再利用10回・GPU再利用6回、64MiB条件でディスク再利用8回・GPU再利用7回を確認しました。64MiB条件では初回の4時刻をディスクから復元し、次の再訪では同じ4時刻をGPUから再利用しました。消去前のキャッシュRAM表示は256MiB条件で253MiB、64MiB条件で63MiBでした。

RAM上限はフレームキャッシュの上限です。YMM4全体、動画デコーダー、GPUのメモリ上限ではありません。64MiB試験では測定時のUIA巡回も除去しているため、2条件の差をRAM上限だけの効果と解釈しません。

## 処理時間

同じstageとUsageで比較したinclusive CPU wall timeです。GPU実行時間、CPU使用時間、Present／音声を含むFPSではありません。ネストしたspanを加算しません。

| RAM上限・操作 | 経路 | n | Update p50（ms） | p95（ms） |
|---|---|---:|---:|---:|
| 256 MiB・off-playback | bypass | 20 | 1332.235 | 2383.768 |
| 256 MiB・cold-playback | render | 15 | 1617.008 | 2974.466 |
| 256 MiB・warm-playback | render | 13 | 1795.134 | 3045.363 |
| 256 MiB・warm-playback | ram | 1 | 10.107 | 10.107 |
| 256 MiB・stress-revisit-1 | render | 4 | 1361.891 | 1783.336 |
| 256 MiB・stress-revisit-1 | ram | 3 | 11.332 | 102.228 |
| 256 MiB・stress-revisit-2 | render | 1 | 1589.075 | 1589.075 |
| 256 MiB・stress-revisit-2 | ram | 2 | 10.512 | 11.191 |
| 256 MiB・stress-revisit-2 | gpu | 4 | 6.027 | 7.753 |
| 64 MiB・off-playback | bypass | 22 | 1124.485 | 2524.516 |
| 64 MiB・cold-playback | render | 17 | 1404.602 | 2297.746 |
| 64 MiB・warm-playback | render | 15 | 1737.565 | 2878.169 |
| 64 MiB・warm-playback | disk | 1 | 7.579 | 7.579 |
| 64 MiB・stress-revisit-1 | render | 4 | 620.060 | 1381.346 |
| 64 MiB・stress-revisit-1 | disk | 4 | 27.774 | 34.384 |
| 64 MiB・stress-revisit-2 | gpu | 5 | 7.921 | 9.302 |

サンプル数の小さい再利用経路の値から、作品全体の速度倍率を推定しません。停止中は表示枠の再計算で再利用直後に通常描画が追加されることもあります。

## 再訪と操作の証拠

- 256 MiB: {"stress-revisit-1": {"RequestedFrames": [16, 59, 60, 138, 206], "ExpectedFrames": [1, 16, 59, 138, 207], "ExactRevisits": 3, "CachedExactUpdates": 3, "Routes": {"ram": 3, "render": 4}}, "stress-revisit-2": {"RequestedFrames": [0, 16, 59, 60, 138, 206], "ExpectedFrames": [1, 16, 59, 138, 207], "ExactRevisits": 3, "CachedExactUpdates": 3, "Routes": {"gpu": 4, "render": 1, "ram": 2}}}
- 256 MiB: {"off-playback": "preview-off", "cold-playback": "preview-on", "warm-playback": "preview-on", "PurgeCompleted": true}
- 64 MiB: {"stress-revisit-1": {"RequestedFrames": [13, 49, 128, 190], "ExpectedFrames": [1, 13, 49, 128, 190], "ExactRevisits": 4, "CachedExactUpdates": 4, "Routes": {"disk": 4, "render": 4}}, "stress-revisit-2": {"RequestedFrames": [0, 13, 49, 128, 190], "ExpectedFrames": [1, 13, 49, 128, 190], "ExactRevisits": 4, "CachedExactUpdates": 4, "Routes": {"gpu": 5}}}
- 64 MiB: {"off-playback": "preview-off", "cold-playback": "preview-on", "warm-playback": "preview-on", "PurgeCompleted": true}

固定座標で狙った5時刻の一部は1フレームずれました。解析は実際に要求された時刻を照合し、初回の時刻と一致した3個以上と、その時刻のGPU／RAM／ディスク経路を検証します。操作名や画像が出たことだけを成功判定に使いません。

## 修正と未解決の性能要因

- fixture配置後にYMM4自身のタイムライン長更新を呼び、900フレーム・421アイテムを確認。
- ドッキング枠がUIAを隠す場合のCI操作を補正。Undo／Redoの前にメインウィンドウへフォーカスを戻し、保存したymmpの個数で検証。
- キャッシュ設定の変更・消去・5秒間隔のキャッシュ統計を詳細ログへ追加。低RAM試験では再生中のUIA巡回を除去。
- 欠落、未完了、破損、例外、停止した再生、実行されなかった編集・消去・再訪を解析の失敗にする。
- 主な負荷はホストのFFmpeg動画更新。キー生成だけを速めても、この負荷は消えません。readbackのMap待ちにも数百msあり、GPU完了を待たない回収方式が今後の改善候補です。
- 本試験は重いfixtureの全900フレームの画素比較、GPU timestamp、音声同期、RTX3060実機FPSを検証していません。既存の実ホストWARP画素一致テストは別に成功しています。

## 同じプロジェクトを開く

ZIPを展開して、展開したフォルダで次を実行します。素材121箇所とプロジェクト自身のパスを書き換え、新しいファイルを作成します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\prepare-stress-project.ps1
```

`stress-30s-local.ymmp`をYMM4で開きます。動画のグリッドと文字が表示され、長さ30秒であることを確認してください。描画キャッシュの詳細ログを開始し、操作名を変更しながら再生・シーク・編集し、停止ボタンから書込完了を待ちます。

キャッシュ既定設定、RAM上限、ログON/OFF、GPU／ドライバー、YMM4／プラグイン版を記録し、CIと実機を分けて比較してください。

evidence内のymmpはCIで保存した編集証拠であり、素材パスがCI用のままです。再生用には上記のlocal版を使います。

## 再現元とログ

- 256 MiB: https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36941205236
  - raw JSONL SHA256: `29aaa0020c5f9e6c309533f43f9b773ae44b0390dff3dbc5205236a106738f7d`
- 64 MiB: https://github.com/sabiasagimp4-ai/YMM4-dlls/actions/runs/36982351471
  - raw JSONL SHA256: `c191836e4b415abe55048bdfd2446cd99d5c50fae89948e3749abcaab3b3bb1f`
- ソース: https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC
- JSONLの生ticksとStopwatchFrequency、統計、プロセス測定、画面、各編集後の保存プロジェクトを同梱。
